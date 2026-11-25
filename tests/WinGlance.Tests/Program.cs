using WinGlance.Core;
using WinGlance.Windows.Identity;
using WinGlance.App.Services;

if (args.Contains("--action-target"))
{
    Thread.Sleep(TimeSpan.FromSeconds(30));
    return;
}

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

SoftwareIdentity Identity(string key = "browser") => new(key, "浏览器", "browser", @"C:\Apps\browser.exe", null, SoftwareCategory.Application, "test");
ProcessSample MakeProcess(int pid, double? cpu, IReadOnlyDictionary<string, double>? gpu = null, long start = 1) => new(
    new(pid, start), Identity(), cpu, 100, 20, 30, 40, 50, gpu ?? new Dictionary<string, double>(), 60, 70);

var processes = new[]
{
    MakeProcess(1, 10, new Dictionary<string, double> { ["gpu0:3d"] = 20, ["gpu0:copy"] = 5 }),
    MakeProcess(2, 15, new Dictionary<string, double> { ["gpu0:3d"] = 30, ["gpu0:copy"] = 10 })
};
var aggregate = SoftwareAggregator.Aggregate(processes).Single();
Check(aggregate.CpuPercent == 25 && aggregate.PrivateWorkingSetBytes == 200, "Software totals must combine independent process values.");
Check(aggregate.GpuPercent == 50, "GPU must sum matching physical engines before choosing the busiest engine.");
Check(SoftwareAggregator.Aggregate([processes[0], MakeProcess(2, null)]).Single().CpuPercent is null, "A missing process observation must not masquerade as a complete application total.");
Check(SoftwareAggregator.Aggregate([processes[0], MakeProcess(2, 5) with { PrivateWorkingSetBytes = null }]).Single().PrivateWorkingSetBytes is null, "An inaccessible member working set must not be silently omitted from the group total.");
Check(SoftwareAggregator.Aggregate([processes[0], MakeProcess(2, 5) with { DedicatedGpuBytes = null }]).Single().DedicatedGpuBytes is null, "Missing per-process VRAM must not be inferred as zero.");
Check(SoftwareAggregator.Aggregate([MakeProcess(1, double.MaxValue), MakeProcess(2, double.MaxValue)]).Single().CpuPercent is null, "Finite inputs whose sum overflows must become unavailable.");
Check(SoftwareAggregator.Aggregate([MakeProcess(1, 0) with { PrivateWorkingSetBytes = -1 }]).Single().PrivateWorkingSetBytes is null, "Negative counter sentinels must not become resource usage.");
Check(SoftwareAggregator.Aggregate([processes[0], MakeProcess(2, 15) with { GpuState = MetricState.Unsupported }]).Single().GpuPercent is null, "Unavailable GPU observations must propagate.");
Check(SoftwareAggregator.Aggregate([processes[0], MakeProcess(2, 15)]).Single().GpuPercent == 20, "A supported GPU process with no active engine should contribute zero.");
Check(SoftwareAggregator.Aggregate([MakeProcess(1, 0)]).Single().GpuPercent == 0, "Known idle GPU observations must display zero.");
Check(SoftwareAggregator.Aggregate([MakeProcess(1, 0) with { GpuState = MetricState.WarmingUp }]).Single().GpuPercent is null, "GPU warm-up must not look like idle.");
Check(SoftwareAggregator.Aggregate([MakeProcess(1, 0, new Dictionary<string, double> { ["gpu0:3d"] = 101 })]).Single().GpuPercent is null, "Invalid over-100 GPU accounting must not be silently clamped.");
Check(SoftwareAggregator.Aggregate([MakeProcess(1, 0, new Dictionary<string, double> { ["gpu0:3d"] = 100.0000001 })]).Single().GpuPercent == 100, "Tiny floating-point overshoot should not invalidate otherwise valid data.");
Check(SoftwareAggregator.Aggregate([processes[0], processes[0]]).Single().CpuPercent == 10, "Duplicate samples of one process instance must not double count.");
Check(SoftwareAggregator.Aggregate([MakeProcess(1, 10, start: 1), MakeProcess(1, 20, start: 2)]).Single().Processes.Count == 2, "PID reuse must retain distinct process identities.");
Check(IdentityPolicy.GroupKey(1, 1, "app", @"C:\Apps\app.exe", 1) != IdentityPolicy.GroupKey(2, 2, "app", @"D:\Other\app.exe", 1), "Same executable basename in different folders must not merge.");
Check(IdentityPolicy.GroupKey(1, 1, "app", @"C:\Apps\app.exe", 1) == IdentityPolicy.GroupKey(2, 2, "app", @"c:/apps/app.exe", 1), "Same application identity should normalize path case and separators.");
Check(IdentityPolicy.GroupKey(1, 1, "msedgewebview2", @"C:\Runtime\msedgewebview2.exe", 1) != IdentityPolicy.GroupKey(2, 2, "msedgewebview2", @"C:\Runtime\msedgewebview2.exe", 1), "A shared WebView runtime must not merge unrelated applications.");
Check(IdentityPolicy.GroupKey(1, 1, "svchost", @"C:\Windows\System32\svchost.exe", 0) != IdentityPolicy.GroupKey(1, 2, "svchost", @"C:\Windows\System32\svchost.exe", 0), "A reused host PID must start a new identity.");
Check(IdentityPolicy.GroupKey(1, 1, "runtimebroker", @"C:\Windows\System32\runtimebroker.exe", 1, "Family|Family!App") == IdentityPolicy.GroupKey(2, 2, "app", @"C:\Apps\app.exe", 1, "Family|Family!App"), "A verified package application identity can group its shared runtime.");
Check(IdentityPolicy.GroupKey(1, 1, "app", @"C:\Apps\app.exe", 1, "Family|Family!AppA") != IdentityPolicy.GroupKey(2, 2, "app", @"C:\Apps\app.exe", 1, "Family|Family!AppB"), "Independent applications within one package must stay separate.");
Check(IdentityPolicy.Classify("msedge", @"C:\Program Files\Edge\msedge.exe", @"C:\Windows", microsoftSignatureVerified: true).Category == SoftwareCategory.Application, "Microsoft-signed user applications must stay visible.");
Check(IdentityPolicy.Classify("svchost", @"C:\Windows\System32\svchost.exe", @"C:\Windows").Category == SoftwareCategory.Unknown, "Unverified core files must stay visible.");
Check(IdentityPolicy.Classify("svchost", @"C:\Windows\System32\svchost.exe", @"C:\Windows", microsoftSignatureVerified: true).Category == SoftwareCategory.WindowsSystem, "Verified known system component should be filterable.");
Check(IdentityPolicy.Classify("svchost", @"C:\Windows-old\System32\svchost.exe", @"C:\Windows", microsoftSignatureVerified: true).Category != SoftwareCategory.WindowsSystem, "Directory prefix lookalikes must not become system files.");
Check(IdentityPolicy.Classify("securityservice", @"C:\Security\service.exe", @"C:\Windows", isService: true).Category == SoftwareCategory.ThirdPartyService, "Third-party services must stay visible.");
Check(IdentityPolicy.Classify("unknown", null, @"C:\Windows").Category == SoftwareCategory.Unknown, "Inaccessible process metadata must not be hidden.");

var catalogs = new CatalogFixture([null, (true, "Microsoft Windows"), (true, "Unused")]);
var catalogPublisher = CatalogTrustSearch.Find(catalogs.Next, catalogs.Verify, catalogs.Release);
Check(catalogPublisher?.MicrosoftVerified == true && catalogPublisher?.Publisher == "Microsoft Windows" && catalogs.Visited.SequenceEqual(new nint[] { 1, 2 }),
    "Catalog lookup must continue after the first candidate fails verification.");
Check(catalogs.Consumed.SequenceEqual(new nint[] { 1 }) && catalogs.Released.SequenceEqual(new nint[] { 2 }) && catalogs.Owned.Count == 0,
    "Catalog advancement consumes the previous context; early success must release only its remaining context.");
catalogs = new CatalogFixture([null, null]);
Check(CatalogTrustSearch.Find(catalogs.Next, catalogs.Verify, catalogs.Release) is null && catalogs.Owned.Count == 0 && catalogs.Released.Count == 0,
    "Catalog exhaustion must not double release contexts consumed by enumeration.");
catalogs = new CatalogFixture([(false, "Other publisher"), (true, "Microsoft Windows")]);
Check(CatalogTrustSearch.Find(catalogs.Next, catalogs.Verify, catalogs.Release)?.MicrosoftVerified == true,
    "A trusted non-Microsoft catalog must not hide a later trusted Microsoft catalog.");
catalogs = new CatalogFixture([null]) { ThrowOnVerify = true };
try { CatalogTrustSearch.Find(catalogs.Next, catalogs.Verify, catalogs.Release); }
catch (InvalidDataException) { }
Check(catalogs.Released.SequenceEqual(new nint[] { 1 }) && catalogs.Owned.Count == 0,
    "An exception during catalog verification must release the current context.");

var history = new HistoryBuffer();
var begin = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
history.Add(begin, [aggregate]);
history.Add(begin.AddSeconds(1), [aggregate]);
history.Add(begin.AddSeconds(61), [aggregate]);
Check(history.Get("browser").Count == 3 && history.Get("browser")[0].Timestamp == begin.AddSeconds(1), "History must prune by actual elapsed time, not sample count, while retaining a gap marker.");
Check(history.Get("browser")[1].CpuPercent is null && history.Get("browser")[1].GpuPercent is null, "Interrupted sampling must break all trend lines.");
history.Add(begin.AddSeconds(60), [aggregate]);
Check(history.Get("browser").Count == 3, "Delayed frames must not draw history backwards.");
history.Add(begin.AddSeconds(61), [aggregate with { CpuPercent = null }]);
Check(history.Get("browser").Count == 3 && history.Get("browser")[^1].CpuPercent is null, "Equal-time snapshots replace; unknown values stay unknown.");
history.Add(begin.AddSeconds(122), []);
Check(history.Get("browser").Count == 0, "Exited applications must age out even without new samples for that application.");
history.Add(begin.AddSeconds(123), [aggregate]);
history.Add(begin.AddSeconds(124), []);
history.Add(begin.AddSeconds(125), [aggregate]);
Check(history.Get("browser").Count == 3 && history.Get("browser")[1].CpuPercent is null, "An application absent from a snapshot must leave a gap even when it reappears quickly.");
Console.WriteLine($"PASS: {checks} regression checks.");

if (args.Contains("--signature-smoke") && OperatingSystem.IsWindows())
{
    var verified = AuthenticodeVerifier.Verify(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe"));
    Check(verified.MicrosoftVerified, "A known installed Windows catalog-signed component must verify.");
    Console.WriteLine($"SIGNATURE PASS: {verified.Publisher}");
}

if (args.Contains("--windows-smoke") && OperatingSystem.IsWindows())
{
    using var resolver = new ProcessIdentityResolver();
    var systemPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe");
    var timeout = DateTime.UtcNow.AddSeconds(30);
    SoftwareIdentity resolved;
    do
    {
        resolved = resolver.Resolve(123, "svchost", systemPath, 0, 123456);
        if (resolved.IconPath is not null) break;
        Thread.Sleep(100);
    } while (DateTime.UtcNow < timeout);
    Check(resolved.IconPath is not null && File.Exists(resolved.IconPath), "Shell icon extraction should produce a cached PNG.");
    Check(File.ReadAllBytes(resolved.IconPath!).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Cached icon must have a valid PNG signature.");
    Check(resolved.Category == SoftwareCategory.WindowsSystem, $"System component signature should verify; actual {resolved.Category}: {resolved.ClassificationReason}");
    Check(resolved.DisplayName != "svchost", "Native executable description should supply a friendly display name.");
    Console.WriteLine($"WINDOWS PASS: {resolved.DisplayName} | {resolved.Publisher} | {resolved.Category} | {resolved.IconPath}");
    Check(ServiceProcessReader.Read().Count > 0, "SCM should enumerate running service process IDs.");
    var packageFound = false;
    foreach (var candidate in System.Diagnostics.Process.GetProcesses())
    {
        using (candidate)
        {
            PackageIdentity? package;
            try
            {
                package = PackageIdentityReader.Read(candidate.Id, candidate.StartTime.ToUniversalTime().Ticks);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
            if (package is null) continue;
            var label = PackageIdentityReader.DisplayName(package.ApplicationId);
            Check(!string.IsNullOrWhiteSpace(label), "A running packaged app should resolve its AppsFolder localized name.");
            var packageIcon = ShellIconCache.Extract("shell:AppsFolder\\" + package.ApplicationId, 0, 0);
            Check(packageIcon is not null, "AppsFolder should provide a package logo without guessing asset paths.");
            Console.WriteLine($"PACKAGE PASS: {label} | {package.ApplicationId} | {packageIcon}");
            packageFound = true;
            break;
        }
    }
    if (!packageFound) Console.WriteLine("PACKAGE SKIP: no accessible running packaged app with application identity.");

    var fixtureDirectory = Path.Combine(Path.GetTempPath(), "WinGlance.IdentityTest." + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixtureDirectory);
    var fixture = Path.Combine(fixtureDirectory, "fixture.exe");
    try
    {
        File.Copy(systemPath, fixture);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var cacheResolver = new ProcessIdentityResolver(clock);
        SoftwareIdentity WaitFor(Func<SoftwareIdentity, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            SoftwareIdentity current;
            do
            {
                current = cacheResolver.Resolve(123, "fixture", fixture, 1, 123456);
                if (predicate(current)) return current;
                Thread.Sleep(50);
            } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException($"Timed out waiting for file metadata cache refresh. Name={current.DisplayName}; Icon={current.IconPath}");
        }
        var first = WaitFor(item => item.IconPath is not null);
        File.Delete(first.IconPath!);
        clock.Advance(TimeSpan.FromMinutes(3));
        WaitFor(item => item.IconPath is not null && File.Exists(item.IconPath));
        Check(File.Exists(first.IconPath), "Evicted/deleted icon files must be repaired on metadata refresh.");
        var replacement = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Copy(replacement, fixture, overwrite: true);
        // Copying an exe outside System32 does not copy its MUI satellite, so inspect the actual fixture.
        var replacementName = System.Diagnostics.FileVersionInfo.GetVersionInfo(fixture).FileDescription;
        clock.Advance(TimeSpan.FromMinutes(3));
        var replaced = WaitFor(item => item.DisplayName == replacementName && item.IconPath != first.IconPath);
        Check(replaced.IconPath is not null && File.Exists(replaced.IconPath), "Executable replacement must refresh both friendly name and icon cache identity.");
        Console.WriteLine("CACHE PASS: deleted icon repaired; replaced executable name and icon invalidated.");
    }
    finally
    {
        File.Delete(fixture);
        Directory.Delete(fixtureDirectory);
    }

    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable path.");
    var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        startInfo.ArgumentList.Add(typeof(TestTimeProvider).Assembly.Location);
    startInfo.ArgumentList.Add("--action-target");
    using var target = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start isolated action test target.");
    try
    {
        var targetIdentity = new ProcessIdentity(target.Id, target.StartTime.ToUniversalTime().Ticks);
        var targetSample = MakeProcess(target.Id, 0) with { Identity = targetIdentity };
        bool Refused(ProcessSample sample)
        {
            try { ProcessActions.Terminate(sample); return false; }
            catch (InvalidOperationException) { return true; }
        }
        Check(Refused(targetSample with { Identity = targetIdentity with { StartTimeUtcTicks = targetIdentity.StartTimeUtcTicks + 1 } }) && !target.HasExited,
            "A mismatched creation time must refuse termination and leave the test process running.");
        Check(Refused(targetSample with { Software = targetSample.Software with { Category = SoftwareCategory.WindowsSystem } }) && !target.HasExited,
            "System-classified processes must be rejected before termination.");
        Check(Refused(targetSample with { Identity = targetIdentity with { Pid = Environment.ProcessId } }),
            "Self termination must be refused before opening a target handle.");
        ProcessActions.Terminate(targetSample);
        Check(target.WaitForExit(5000), "A verified isolated test process should terminate through its validated handle.");
        Console.WriteLine("ACTIONS PASS: stale identity, system and self protection; verified isolated process termination.");
    }
    finally { if (!target.HasExited) { target.Kill(); target.WaitForExit(5000); } }
}

sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
}

sealed class CatalogFixture(IReadOnlyList<(bool MicrosoftVerified, string? Publisher)?> candidates)
{
    public HashSet<nint> Owned { get; } = [];
    public List<nint> Visited { get; } = [];
    public List<nint> Consumed { get; } = [];
    public List<nint> Released { get; } = [];
    public bool ThrowOnVerify { get; init; }
    public nint Next(nint previous)
    {
        if (previous != 0)
        {
            if (!Owned.Remove(previous)) throw new InvalidOperationException("Invalid/double-released enumeration context.");
            Consumed.Add(previous);
        }
        nint next = previous + 1;
        if (next > candidates.Count) return 0;
        Owned.Add(next);
        return next;
    }
    public (bool MicrosoftVerified, string? Publisher)? Verify(nint catalog)
    {
        Visited.Add(catalog);
        if (ThrowOnVerify) throw new InvalidDataException("Controlled verification failure.");
        return candidates[(int)catalog - 1];
    }
    public void Release(nint catalog)
    {
        if (!Owned.Remove(catalog)) throw new InvalidOperationException("Invalid/double release.");
        Released.Add(catalog);
    }
}
