using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using WinGlance.Core;
using WinGlance.Statistics;

internal static class StatisticsExportTests
{
    internal static IEnumerable<(string Name, Func<string, Task> Run)> Cases =>
    [
        ("Export captures one midnight clock and bypasses cached query", ClockBoundary),
        ("Export queue cancellation skips execution without storage warning", QueueCancellation),
        ("Export reset ordering and immutable detached data", ResetOrdering),
        ("Export filters, four-column sort and all-app denominators", Filtering),
        ("CSV BOM, numeric fidelity, formula prefixes and RFC4180 round trip", Csv),
        ("JSON retains original text, units, gaps and no executable paths", Json),
        ("Atomic export real new, replacement, sharing failure and target races", RealCommit),
        ("Replace partial failure and recovery decision matrix", RecoveryMatrix),
        ("Writer cancellation and I/O failures preserve target and own temporary cleanup", WriterFailures),
        ("Export cancellation versus commit and independent completion", CancellationRace)
    ];

    private static readonly DateTimeOffset Origin = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    private static StatisticsExportRequest Request(bool hide = false, string search = "", string view = "network", string sort = "first", bool descending = true) =>
        new(StatisticsRange.Today, hide, search, view, sort, descending, Origin);
    private static SoftwareIdentity Software(string name, SoftwareCategory category = SoftwareCategory.Application) =>
        new("file:" + name, name, name + ".exe", "C:\\PrivateApps\\" + name + ".exe", "Publisher", category, "fixture");
    private static Dictionary<StatisticMetric, MetricAccounting> Metrics(long bytes, double memory = 100, double vram = 50) => new()
    {
        [StatisticMetric.Cpu] = new(10, 1, 10, CpuTicksDelta: 50),
        [StatisticMetric.Download] = new(bytes, 1, bytes, BytesDelta: bytes),
        [StatisticMetric.Upload] = new(0, 1, 0, BytesDelta: 0),
        [StatisticMetric.Memory] = new(memory, 1, memory),
        [StatisticMetric.DedicatedGpuMemory] = new(vram, 1, vram)
    };
    private static MonitorSnapshot Frame(Guid run, long sequence, DateTimeOffset end, params (SoftwareIdentity Software, Dictionary<StatisticMetric, MetricAccounting> Metrics)[] apps)
    {
        var processes = apps.Select((app, index) => new ProcessAccounting(new(10 + index, 1), app.Software, 1, app.Metrics)).ToArray();
        var live = processes.Select(app => new ProcessSample(app.Identity, app.Software, 0, 100, 0, 0, 0, 0,
            new Dictionary<string, double>(), 0, 0)).ToArray();
        return new(end, 1, new(10, 1000, 500, 0, 0, 0, 0, 0, "test"), live, new Dictionary<string, Capability>(), false)
        { Accounting = new(run, sequence, end.AddSeconds(-1), end, 1, processes,
            new(Metrics(1000), NetworkScope: "fixture", TotalMemoryBytes: 1000, DedicatedGpuCapacityBytes: 500)) };
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<StatisticsExportSnapshot> Sample(string path, string name = "Example")
    {
        var clock = new Clock { Now = Origin };
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        service.Accept(Frame(Guid.NewGuid(), 1, Origin, (Software(name), Metrics(100))));
        return await service.CaptureExportAsync(Request());
    }
    private static async Task ClockBoundary(string path)
    {
        var dayEnd = new DateTimeOffset(2026, 11, 28, 23, 59, 59, TimeSpan.Zero);
        var clock = new Clock { Now = dayEnd };
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        service.Accept(Frame(Guid.NewGuid(), 1, dayEnd, (Software("Before midnight"), Metrics(101))));
        var cached = await service.QueryAsync(StatisticsRange.Today);
        clock.Now = dayEnd.AddMilliseconds(500); clock.Reads = 0; clock.Step = TimeSpan.FromSeconds(2);
        var exported = await service.CaptureExportAsync(Request());
        Require(clock.Reads == 1, "Export must read its clock exactly once.");
        Require(exported.AsOfUtc == dayEnd.AddMilliseconds(500) && exported.EndUtc == exported.AsOfUtc, "Export reused cached timestamp.");
        Require(exported.StartUtc.Day == 28 && exported.Applications.Single().Metrics[StatisticMetric.Download].Bytes == 101, "Midnight range mixed two clock reads.");
        Require(cached.EndUtc != exported.EndUtc, "Fixture did not exercise cached timestamp.");
        clock.Step = TimeSpan.Zero;
    }
    private static async Task QueueCancellation(string path)
    {
        var clock = new Clock { Now = Origin };
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        await service.QueryAsync(StatisticsRange.Today);
        clock.BlockNext = true;
        var blocker = service.CaptureExportAsync(Request());
        await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var skipped = service.CaptureExportAsync(Request(), cancelled.Token);
        cancelled.Cancel();
        clock.Release.Set();
        await blocker;
        try { await skipped; throw new InvalidOperationException("Queued capture did not cancel."); }
        catch (OperationCanceledException) { }
        int before = clock.Reads;
        var after = await service.QueryAsync(StatisticsRange.Today);
        Require(after.Warning is null, "Cancellation polluted storage warning.");
        Require(clock.Reads == before + 1, "Cancelled queued capture executed an action.");
    }
    private static async Task ResetOrdering(string path)
    {
        var clock = new Clock { Now = Origin };
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var run = Guid.NewGuid(); service.Accept(Frame(run, 1, Origin, (Software("App"), Metrics(100))));
        var beforeTask = service.CaptureExportAsync(Request());
        var resetTask = service.ResetAsync(StatisticsRange.Today);
        var afterTask = service.CaptureExportAsync(Request());
        var before = await beforeTask; await resetTask; var after = await afterTask;
        Require(before.Applications.Count == 1 && after.Applications.Count == 0, "Reset/capture order was not serialized.");
        clock.Now = Origin.AddSeconds(2);
        service.Accept(Frame(run, 2, clock.Now, (Software("App"), Metrics(300))));
        var later = await service.CaptureExportAsync(Request());
        Require(before.Applications.Single().Metrics[StatisticMetric.Download].Bytes == 100 && later.Applications.Single().Metrics[StatisticMetric.Download].Bytes == 300,
            "Export snapshot retained mutable aggregation references.");
        Require(before.Applications is not StatisticsExportApplication[], "Export array is mutable.");
        try { ((IDictionary<StatisticMetric, StatisticsExportMetric>)before.System.Metrics).Clear(); throw new Exception("Mutable metrics"); }
        catch (NotSupportedException) { }
    }
    private static async Task Filtering(string path)
    {
        var clock = new Clock { Now = Origin };
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var a = Software("Alpha"); var b = Software("Beta", SoftwareCategory.WindowsSystem);
        var unknown = Software("Unknown");
        service.Accept(Frame(Guid.NewGuid(), 1, Origin, (a, Metrics(100, 700, 20)), (b, Metrics(300, 200, 90)),
            (unknown, new Dictionary<StatisticMetric, MetricAccounting>())));
        var filtered = await service.CaptureExportAsync(Request(hide: true, search: "Alpha"));
        Require(filtered.Scope.AllApplicationsCount == 3 && filtered.Applications.Count == 1 && filtered.Scope.AttributableNetworkBytes == 400, "Filter altered denominator.");
        Require(filtered.Applications[0].NetworkShare == .25m && filtered.System.Metrics[StatisticMetric.Download].Bytes == 1000, "System or share was filtered.");
        var byMemory = await service.CaptureExportAsync(Request(view: "overview", sort: "fourth"));
        var byVram = await service.CaptureExportAsync(Request(view: "compute", sort: "fourth"));
        Require(byMemory.Applications[0].DisplayName == "Alpha" && byVram.Applications[0].DisplayName == "Beta", "Fourth column mapping was wrong.");
        var ascending = await service.CaptureExportAsync(Request(view: "compute", sort: "fourth", descending: false));
        Require(ascending.Applications[^1].DisplayName == "Unknown", "Unknown values must remain last in either sort direction.");
        Require((await service.CaptureExportAsync(Request(search: "PrivateApps\\Alpha"))).Applications.Count == 1, "Path filter does not match UI.");
    }
    private static async Task Csv(string path)
    {
        var data = await Sample(path, "=SUM(1,2)\r\n\"测试\"");
        data = data with { Scope = data.Scope with { Search = "\u200b=SEARCH" }, System = data.System with { NetworkScope = "＠source" },
            Applications = data.Applications.Select(a => a with { ProcessName = "\t=cmd", Publisher = "+publisher" }).ToList().AsReadOnly() };
        string destination = Path.Combine(Path.GetDirectoryName(path)!, "sample.csv");
        var operation = Operation(StatisticsExportFormat.Csv);
        var oldCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        StatisticsExportResult result;
        try { result = await StatisticsExporter.WriteAsync(operation, data, StatisticsExportDestination.CreateNew(destination)); }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        Require(result.Phase == StatisticsExportPhase.Completed && result.Warning?.Contains("7 个") == true, "Formula-prefix warning/count missing.");
        Require(File.ReadAllBytes(destination).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "CSV must contain UTF-8 BOM.");
        using var parser = new TextFieldParser(destination, Encoding.UTF8) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var header = parser.ReadFields()!; var system = parser.ReadFields()!; var app = parser.ReadFields()!;
        Require(parser.EndOfData && system.Length == header.Length && app.Length == header.Length, "CSV record boundaries or column count changed.");
        string field(string key) => app[Array.IndexOf(header, key)];
        Require(field("display_name") == "文本：=SUM(1,2)\r\n\"测试\"" && field("process_name") == "文本：\t=cmd", "Quoted multiline/leading-control text did not round-trip.");
        Require(field("network_share") == "1" && field("Upload_bytes") == "0" && field("Gpu_average") == "", "Null/zero/numbers were not preserved.");
        foreach (string value in new[] { "=x", "+x", "-x", "@x", "＝x", "＋x", "－x", "＠x", "\rtest", "\ntest", "\ttest", " test", "\u00a0test", "\u200btest" })
            Require(StatisticsExporter.NeedsProtection(value), "Missing formula/control protection.");
        Require(!StatisticsExporter.NeedsProtection("普通文本") && !StatisticsExporter.NeedsProtection(""), "Ordinary text changed unnecessarily.");
    }
    private static async Task Json(string path)
    {
        var data = await Sample(path, "=original");
        string destination = Path.Combine(Path.GetDirectoryName(path)!, "sample.json");
        var result = await StatisticsExporter.WriteAsync(Operation(), data, StatisticsExportDestination.CreateNew(destination));
        Require(result.Phase == StatisticsExportPhase.Completed && result.Warning is null, "JSON should not sanitize text.");
        string raw = File.ReadAllText(destination); using var document = JsonDocument.Parse(raw);
        var app = document.RootElement.GetProperty("applications")[0];
        Require(app.GetProperty("displayName").GetString() == "=original", "JSON original text changed.");
        Require(!raw.Contains("PrivateApps") && !raw.Contains("filePath") && !raw.Contains("iconPath"), "Export leaked executable/icon paths.");
        Require(app.GetProperty("metrics").GetProperty("Gpu").GetProperty("average").ValueKind == JsonValueKind.Null, "Missing metric became zero.");
        Require(app.GetProperty("metrics").GetProperty("Memory").GetProperty("integralUnit").GetString() == "byte_seconds", "Metric unit missing.");
    }

    private static async Task RealCommit(string path)
    {
        var data = await Sample(path);
        string directory = Path.GetDirectoryName(path)!;
        string destination = Path.Combine(directory, "export.json");
        var result = await StatisticsExporter.WriteAsync(Operation(), data, StatisticsExportDestination.CreateNew(destination));
        Require(result.Phase == StatisticsExportPhase.Completed && JsonDocument.Parse(File.ReadAllText(destination)).RootElement.GetProperty("schemaVersion").GetInt32() == 1, "New export failed.");
        File.WriteAllText(destination, "original");
        result = await StatisticsExporter.WriteAsync(Operation(), data, StatisticsExportDestination.ConfirmedOverwrite(destination));
        Require(result.Phase == StatisticsExportPhase.Completed && !Directory.EnumerateDirectories(directory, ".winglance-recovery-*").Any(), "Real replace did not clean its backup.");

        File.WriteAllText(destination, "locked original");
        var selected = StatisticsExportDestination.ConfirmedOverwrite(destination);
        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await StatisticsExporter.WriteAsync(Operation(), data, selected);
        Require(result.Phase == StatisticsExportPhase.Failed && File.ReadAllText(destination) == "locked original", "Sharing violation damaged the original.");

        string racePath = Path.Combine(directory, "race.json");
        selected = StatisticsExportDestination.CreateNew(racePath);
        File.WriteAllText(racePath, "other writer");
        result = await StatisticsExporter.WriteAsync(Operation(), data, selected);
        Require(result.Phase == StatisticsExportPhase.Failed && File.ReadAllText(racePath) == "other writer", "New-file race overwrote another writer.");

        selected = StatisticsExportDestination.ConfirmedOverwrite(destination);
        File.Delete(destination);
        result = await StatisticsExporter.WriteAsync(Operation(), data, selected);
        Require(result.Phase == StatisticsExportPhase.Failed && !File.Exists(destination), "Missing existing target downgraded to new-file move.");
        try { ExportFileSystem.Instance.Probe(directory); throw new Exception("Directory accepted as file."); }
        catch (IOException) { }
        try { ExportFileSystem.Instance.Probe(Path.Combine(directory, "missing-parent", "child.json")); throw new Exception("Unknown parent treated as missing file."); }
        catch (DirectoryNotFoundException) { }
    }

    private static Task RecoveryMatrix(string path)
    {
        string root = Path.GetDirectoryName(path)!;
        foreach (string scenario in new[] { "1175", "1176", "restore", "race", "denied", "unknown", "inconsistent-1177", "unexpected-backup", "non-win32", "cleanup", "directory-cleanup" })
        {
            string folder = Path.Combine(root, scenario); Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "existing.json"), temporary = Path.Combine(folder, "owned.tmp");
            File.WriteAllText(destination, "original bytes"); File.WriteAllText(temporary, "new bytes");
            var target = StatisticsExportDestination.ConfirmedOverwrite(destination);
            var files = new FaultFiles(); string? backup = null;
            files.Replacing = (source, targetPath, saved) =>
            {
                backup = saved;
                if (scenario == "inconsistent-1177") throw new IOException("injected inconsistent 1177", unchecked((int)(0x80070000u | 1177u)));
                if (scenario is "1175" or "1176") throw new IOException("injected", unchecked((int)(0x80070000u | uint.Parse(scenario))));
                if (scenario is "cleanup" or "directory-cleanup") { ExportFileSystem.Instance.Replace(source, targetPath, saved); return; }
                File.Move(targetPath, saved);
                if (scenario == "non-win32") throw new IOException("Non-Win32 low bits must not trigger restore", 1177);
                throw new IOException("injected", unchecked((int)(0x80070000u | (scenario == "unexpected-backup" ? 5u : 1177u))));
            };
            files.Moving = (source, targetPath) =>
            {
                if (source == backup && scenario == "race") File.WriteAllText(targetPath, "concurrent writer");
                if (source == backup && scenario == "denied") throw new UnauthorizedAccessException("injected recovery denial");
                ExportFileSystem.Instance.Move(source, targetPath);
            };
            files.Probing = candidate =>
            {
                if (candidate == backup && scenario == "unknown") throw new IOException("injected unknown state");
                return ExportFileSystem.Instance.Probe(candidate);
            };
            files.Deleting = candidate =>
            {
                if (candidate == backup && scenario == "cleanup") throw new UnauthorizedAccessException("injected backup cleanup denial");
                ExportFileSystem.Instance.DeleteFile(candidate);
            };
            files.DeletingDirectory = candidate =>
            {
                if (scenario == "directory-cleanup") throw new UnauthorizedAccessException("injected directory cleanup denial");
                ExportFileSystem.Instance.DeleteDirectory(candidate);
            };
            var result = StatisticsExportCommitter.Commit(temporary, target, files);
            if (scenario is "cleanup" or "directory-cleanup")
                Require(result.Phase == StatisticsExportPhase.Completed && result.Warning is not null && result.RecoveryPath is not null && File.ReadAllText(destination) == "new bytes", scenario);
            else if (scenario is "1175" or "1176" or "restore")
                Require(result.Phase == StatisticsExportPhase.Failed && File.ReadAllText(destination) == "original bytes" && !File.Exists(temporary), scenario);
            else if (scenario == "inconsistent-1177")
                Require(result.Phase == StatisticsExportPhase.Failed && result.RecoveryPath is not null && File.Exists(temporary) && File.ReadAllText(destination) == "original bytes", scenario);
            else
            {
                Require(result.Phase == StatisticsExportPhase.Failed && result.RecoveryPath is not null && backup is not null &&
                    File.ReadAllText(backup) == "original bytes" && File.Exists(temporary), scenario);
                if (scenario == "race") Require(File.ReadAllText(destination) == "concurrent writer", "Recovery overwrote new destination.");
                if (scenario == "unknown") Require(result.RecoveryPath == backup, "Unknown backup state must retain its precise recovery path.");
            }
        }
        return Task.CompletedTask;
    }

    private static async Task CancellationRace(string path)
    {
        var data = await Sample(path);
        string destination = Path.Combine(Path.GetDirectoryName(path)!, "race.json");
        var selecting = new StatisticsExportOperation(Request(), StatisticsExportFormat.Json);
        Require(selecting.TryCancel() && (await selecting.Completion).Phase == StatisticsExportPhase.Cancelled && selecting.WriteTask is null && !selecting.TryBeginCapture(), "Selecting cancellation must finish independently.");
        var cancelled = Operation(); cancelled.TryCancel();
        var cancellation = await StatisticsExporter.WriteAsync(cancelled, data, StatisticsExportDestination.CreateNew(destination));
        Require(cancellation.Phase == StatisticsExportPhase.Cancelled && !File.Exists(destination), "Pre-write cancellation touched destination.");

        var writing = Operation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = writing.StartWriting(async () =>
        {
            entered.TrySetResult(); await release.Task;
            Require(!writing.TryBeginCommit(), "Cancellation lost before commit gate.");
            return new(StatisticsExportPhase.Cancelled);
        });
        Require(ReferenceEquals(task, writing.WriteTask), "Writer was not synchronously registered.");
        await entered.Task; Require(writing.TryCancel() && !writing.Completion.IsCompleted, "Writing cancellation completed before writer cleanup.");
        release.TrySetResult(); Require((await writing.Completion).Phase == StatisticsExportPhase.Cancelled, "Writing cancellation did not complete.");

        var committing = Operation();
        var committedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitRelease = new ManualResetEventSlim();
        var files = new FaultFiles { Moving = (source, target) =>
        {
            committedEntered.TrySetResult();
            if (!commitRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Commit gate");
            ExportFileSystem.Instance.Move(source, target);
        } };
        var actualWriter = StatisticsExporter.WriteAsync(committing, data, StatisticsExportDestination.CreateNew(destination), files);
        Require(ReferenceEquals(actualWriter, committing.WriteTask), "Public writer did not register before returning.");
        await committedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(committing.Phase == StatisticsExportPhase.Committing && !committing.TryCancel(), "Cancel won after commit started.");
        commitRelease.Set();
        var completed = await actualWriter;
        Require(completed.Phase == StatisticsExportPhase.Completed && ReferenceEquals(await committing.Completion, completed), "Completion depends on UI continuation or contradicts writer result.");
    }

    private static async Task WriterFailures(string path)
    {
        var snapshot = await Sample(path); string directory = Path.GetDirectoryName(path)!;
        string target = Path.Combine(directory, "original.json"); File.WriteAllText(target, "keep original");
        var selected = StatisticsExportDestination.ConfirmedOverwrite(target);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var slow = snapshot with { System = snapshot.System with { Metrics = new InterceptMetrics(snapshot.System.Metrics, () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Writer gate");
        }) } };
        var operation = Operation();
        var writer = StatisticsExporter.WriteAsync(operation, slow, selected);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(operation.TryCancel() && !operation.Completion.IsCompleted, "In-flight write cancellation completed before cleanup.");
        release.Set();
        var cancelled = await writer;
        Require(cancelled.Phase == StatisticsExportPhase.Cancelled && File.ReadAllText(target) == "keep original" &&
            !Directory.EnumerateFiles(directory, ".winglance-export-*.tmp").Any(), "Cancellation touched target or left its temp.");

        var failedData = snapshot with { System = snapshot.System with { Metrics = new InterceptMetrics(snapshot.System.Metrics,
            () => throw new IOException("injected output failure")) } };
        var failed = await StatisticsExporter.WriteAsync(Operation(), failedData, selected);
        Require(failed.Phase == StatisticsExportPhase.Failed && File.ReadAllText(target) == "keep original" &&
            !Directory.EnumerateFiles(directory, ".winglance-export-*.tmp").Any(), "Write failure damaged target or did not clean temp.");
        var cleanupDenied = new FaultFiles { Deleting = _ => throw new UnauthorizedAccessException("injected temp cleanup denial") };
        var residue = await StatisticsExporter.WriteAsync(Operation(), failedData, selected, cleanupDenied);
        Require(residue.Phase == StatisticsExportPhase.Failed && residue.RecoveryPath is { } remaining && File.Exists(remaining) &&
            residue.Warning is not null && File.ReadAllText(target) == "keep original", "Temp cleanup failure omitted recovery path.");
    }

    private sealed class InterceptMetrics(IReadOnlyDictionary<StatisticMetric, StatisticsExportMetric> values, Action intercept)
        : IReadOnlyDictionary<StatisticMetric, StatisticsExportMetric>
    {
        public StatisticsExportMetric this[StatisticMetric key] => values[key];
        public IEnumerable<StatisticMetric> Keys => values.Keys;
        public IEnumerable<StatisticsExportMetric> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(StatisticMetric key) => values.ContainsKey(key);
        public bool TryGetValue(StatisticMetric key, out StatisticsExportMetric value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<StatisticMetric, StatisticsExportMetric>> GetEnumerator()
        { intercept(); return values.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FaultFiles : IExportFileSystem
    {
        internal Func<string, ExportPathState>? Probing;
        internal Action<string, string, string>? Replacing;
        internal Action<string, string>? Moving;
        internal Action<string>? Deleting, DeletingDirectory;
        public ExportPathState Probe(string path) => Probing?.Invoke(path) ?? ExportFileSystem.Instance.Probe(path);
        public string CreateRecoveryDirectory(string parent) => ExportFileSystem.Instance.CreateRecoveryDirectory(parent);
        public void Move(string source, string destination) { if (Moving is { } action) action(source, destination); else ExportFileSystem.Instance.Move(source, destination); }
        public void Replace(string source, string destination, string backup) { if (Replacing is { } action) action(source, destination, backup); else ExportFileSystem.Instance.Replace(source, destination, backup); }
        public void DeleteFile(string path) { if (Deleting is { } action) action(path); else ExportFileSystem.Instance.DeleteFile(path); }
        public void DeleteDirectory(string path) { if (DeletingDirectory is { } action) action(path); else ExportFileSystem.Instance.DeleteDirectory(path); }
    }

    private static StatisticsExportOperation Operation(StatisticsExportFormat format = StatisticsExportFormat.Json)
    { var operation = new StatisticsExportOperation(Request(), format); Require(operation.TryBeginCapture(), "Capture phase failed."); return operation; }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now; internal TimeSpan Step; internal int Reads; internal bool BlockNext;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release = new();
        public override DateTimeOffset GetUtcNow()
        {
            if (BlockNext) { BlockNext = false; Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test clock gate"); }
            Reads++; var result = Now; Now += Step; return result;
        }
    }
}
