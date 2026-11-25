using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WinGlance.App.Services;
using WinGlance.App.ViewModels;
using WinGlance.Core;

[assembly: SupportedOSPlatform("windows")]

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
    passed++;
}

static async Task Until(Func<bool> condition, string message)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition())
    {
        if (timeout.IsCancellationRequested) throw new TimeoutException(message);
        await Task.Delay(10);
    }
}

static ProcessSample ProcessRow(int pid, string? path) => new(new(pid, pid * 100L),
    new("software", "示例软件", "example", path, null, SoftwareCategory.Application, "fixture"),
    0, 0, 0, 0, 0, 0, new Dictionary<string, double>(), 0, 0);

var main = ProcessRow(1101, @"C:\Main\main.exe");
var child = ProcessRow(2202, @"C:\Child\child.exe");
var inaccessibleChild = ProcessRow(3303, null);
var group = new SoftwareSample("software", main.Software, [main, child, inaccessibleChild], 0, 0, 0, 0, 0, 0, 0, 0, 0);
string tooltip = ProcessPresentation.Tooltip("子进程", group, child);
Check(tooltip.Contains(child.Software.FilePath!) && tooltip.EndsWith("PID: 2202") && !tooltip.Contains("1101") && !tooltip.Contains(main.Software.FilePath!),
    "child tooltip contains only its own PID and executable path");
tooltip = ProcessPresentation.Tooltip("不可访问子进程", group, inaccessibleChild);
Check(tooltip.Contains("路径不可访问") && tooltip.EndsWith("PID: 3303") && !tooltip.Contains(main.Software.FilePath!),
    "inaccessible child path never falls back to the group executable");
Check(ProcessPresentation.Tooltip("软件", group, null).EndsWith("PID: 1101, 2202, 3303"),
    "group tooltip lists all process IDs");

var system = new SystemSample(12, 1000, 400, 10, 20, 30, 40, 0, "自动网卡")
{
    DefaultNetworkInterfaceId = "automatic",
    NetworkInterfaces =
    [
        new("automatic", "自动网卡", 10, 20, MetricState.Ready, true, true),
        new("chosen", "手选网卡", 50, 60, MetricState.Ready, false, true),
        new("zero", "空闲网卡", 0, 0, MetricState.Ready, true, true),
        new("disconnected", "断开网卡", 999, 999, MetricState.Ready, true, false),
        new("warming", "预热网卡", 999, 999, MetricState.WarmingUp, true, true)
    ],
    PhysicalDisks =
    [
        new("0 C:", "磁盘 0", 70, 80, MetricState.Ready),
        new("1 D:", "磁盘 1", 0, 0, MetricState.Ready),
        new("2 E:", "磁盘 2", 999, 999, MetricState.WarmingUp)
    ]
};
Check(ReferenceEquals(OverviewSelection.Apply(system, null, null), system), "automatic overview selection preserves the original system sample");
var selected = OverviewSelection.Apply(system, "chosen", "0 C:");
Check(selected.DownloadBytesPerSecond == 50 && selected.UploadBytesPerSecond == 60 && selected.DiskReadBytesPerSecond == 70 && selected.DiskWriteBytesPerSecond == 80,
    "manual overview selection uses the selected devices");
Check(selected.CpuPercent == system.CpuPercent && selected.GpuPercent == 0 && selected.DefaultNetworkInterfaceId == "automatic" &&
    ReferenceEquals(selected.NetworkInterfaces, system.NetworkInterfaces), "selection preserves CPU, valid GPU zero, default identity, and device samples");
selected = OverviewSelection.Apply(system, "missing", "missing");
Check(selected.DownloadBytesPerSecond is null && selected.UploadBytesPerSecond is null && selected.DiskReadBytesPerSecond is null && selected.DiskWriteBytesPerSecond is null,
    "missing selected devices stay unavailable instead of falling back to automatic totals");
selected = OverviewSelection.Apply(system, "disconnected", "2 E:");
Check(selected.DownloadBytesPerSecond is null && selected.UploadBytesPerSecond is null && selected.DiskReadBytesPerSecond is null && selected.DiskWriteBytesPerSecond is null,
    "disconnected and warming devices do not expose stale rates");
Check(OverviewSelection.Apply(system, "warming", null).DownloadBytesPerSecond is null, "warming interface remains unavailable");
selected = OverviewSelection.Apply(system, "zero", "1 D:");
Check(selected.DownloadBytesPerSecond == 0 && selected.UploadBytesPerSecond == 0 && selected.DiskReadBytesPerSecond == 0 && selected.DiskWriteBytesPerSecond == 0,
    "ready device zeros remain valid measured values");

WorkArea[] areas = [new(-1920, -200, 1920, 1040), new(0, 0, 2560, 1400)];
Check(OverlayPlacement.Recover(-1800, -100, 280, 80, areas) == (-1800, -100), "overlay preserves a visible negative-coordinate monitor position");
Check(OverlayPlacement.Recover(-2500, -500, 280, 80, areas) == (-1920, -200), "overlay recovers offscreen coordinates to the nearest monitor work area");
Check(OverlayPlacement.Recover(2500, 1390, 280, 80, areas) == (2280, 1320), "overlay keeps its entire rectangle inside the work area");
Check(OverlayPlacement.Recover(-2000, 0, 3000, 2000, [areas[0]]) == (-1920, -200), "oversized overlay recovers without invalid clamp bounds");
Check(OverlayPlacement.Recover(15, 25, 280, 80, []) == (15, 25), "missing monitor data preserves the saved position");

var live = new LiveSnapshotStore();
var start = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
for (int i = 0; i < 70; i++)
    live.Accept(Snapshots.Create(false) with { Timestamp = start.AddSeconds(i), Processes = [main] });
var systemHistory = live.SystemHistory();
var softwareHistory = live.History.Get("software");
Check(systemHistory.Length == 61 && systemHistory[0].Timestamp == start.AddSeconds(9) && systemHistory[^1].Timestamp == start.AddSeconds(69),
    "70 background snapshots preserve exactly the latest 60-second system history window");
Check(softwareHistory.Count == 61 && softwareHistory[0].Timestamp == start.AddSeconds(9) && softwareHistory[^1].Timestamp == start.AddSeconds(69),
    "background collection preserves software history without any UI rendering");
Check(systemHistory.All(p => p.Cpu == 0 && p.Gpu == 0) && softwareHistory.All(p => p.CpuPercent == 0 && p.GpuPercent == 0),
    "background system and software history retain measured CPU and GPU zeros");
var latest = live.Latest;
live.Accept(Snapshots.Create(false) with { Timestamp = start.AddSeconds(30) });
live.Accept(Snapshots.Create(false) with { Timestamp = start.AddSeconds(69) });
Check(ReferenceEquals(live.Latest, latest) && live.SystemHistory().Length == 61 && live.History.Get("software").Count == 61,
    "late and duplicate snapshots cannot replace the latest frame or mutate retained history");

MonitorSnapshot TrackedSnapshot(Guid run, long sequence, DateTimeOffset timestamp) => Snapshots.Create(false) with
{
    Timestamp = timestamp,
    Processes = [main],
    Accounting = new(run, sequence, timestamp.AddSeconds(-1), timestamp, 1, [])
};
var sequencedLive = new LiveSnapshotStore();
var firstRun = Guid.NewGuid();
sequencedLive.Accept(TrackedSnapshot(firstRun, 1, start.AddSeconds(100)));
sequencedLive.Accept(TrackedSnapshot(firstRun, 2, start.AddSeconds(101)));
var rollback = TrackedSnapshot(firstRun, 3, start.AddSeconds(50));
sequencedLive.Accept(rollback);
Check(ReferenceEquals(sequencedLive.Latest?.Snapshot, rollback),
    "increasing collector sequence accepts a snapshot after wall-clock rollback");
Check(sequencedLive.SystemHistory() is [{ Timestamp: var systemTime, Cpu: 0, Gpu: 0 }] && systemTime == rollback.Timestamp &&
    sequencedLive.History.Get("software") is [{ Timestamp: var softwareTime, CpuPercent: 0, GpuPercent: 0 }] && softwareTime == rollback.Timestamp,
    "wall-clock rollback rebuilds both live history windows while preserving measured zeros");
var beforeStale = sequencedLive.Latest;
sequencedLive.Accept(TrackedSnapshot(firstRun, 3, start.AddSeconds(200)));
Check(ReferenceEquals(sequencedLive.Latest, beforeStale) && sequencedLive.SystemHistory().Length == 1 && sequencedLive.History.Get("software").Count == 1,
    "duplicate collector sequence is rejected even with a newer wall-clock timestamp");
sequencedLive.Accept(TrackedSnapshot(firstRun, 2, start.AddSeconds(201)));
Check(ReferenceEquals(sequencedLive.Latest, beforeStale) && sequencedLive.SystemHistory().Length == 1 && sequencedLive.History.Get("software").Count == 1,
    "lower collector sequence is rejected even with a newer wall-clock timestamp");
var secondRun = Guid.NewGuid();
var replacementSnapshot = TrackedSnapshot(secondRun, 1, start.AddSeconds(51));
sequencedLive.Accept(replacementSnapshot);
Check(ReferenceEquals(sequencedLive.Latest?.Snapshot, replacementSnapshot) && sequencedLive.SystemHistory().Length == 2,
    "replacement collector run accepts its restarted sequence");
var beforeRetired = sequencedLive.Latest;
sequencedLive.Accept(TrackedSnapshot(firstRun, 999, start.AddSeconds(300)));
Check(ReferenceEquals(sequencedLive.Latest, beforeRetired) && sequencedLive.SystemHistory().Length == 2 && sequencedLive.History.Get("software").Count == 2,
    "retired collector run cannot replace live state with a late higher-sequence snapshot");

int stops = 0, flushes = 0, finishes = 0;
var reports = new ConcurrentQueue<string>();
var pendingFlush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var coordinator = new SessionEndCoordinator(() => stops++, () => { flushes++; return pendingFlush.Task; },
    () => { finishes++; return pendingFinish.Task; }, reports.Enqueue);
coordinator.Query();
coordinator.Query();
coordinator.End(false, TimeSpan.Zero);
Check(stops == 0 && finishes == 0 && flushes == 1, "session query and cancelled end keep collection running and coalesce an active flush");
pendingFlush.SetResult();
await pendingFlush.Task;
await Task.Delay(10);
var final = coordinator.FinishAsync();
Check(ReferenceEquals(final, coordinator.FinishAsync()) && stops == 1 && finishes == 1, "confirmed session finish is idempotent");
coordinator.Query();
Check(flushes == 1, "session query after final save cannot start another flush");
var elapsed = Stopwatch.StartNew();
coordinator.End(true, TimeSpan.FromMilliseconds(40));
elapsed.Stop();
Check(elapsed.Elapsed < TimeSpan.FromSeconds(1) && reports.Any(x => x.Contains("超过等待上限")), "session end waits only for its bounded timeout and reports an unfinished save");
pendingFinish.SetResult();
await final.WaitAsync(TimeSpan.FromSeconds(2));
coordinator.End(true, TimeSpan.Zero);
Check(stops == 1 && finishes == 1, "repeated confirmed end does not repeat stop or final save");

var failures = new ConcurrentQueue<string>();
var failed = new SessionEndCoordinator(() => { }, () => throw new IOException("flush-fixture"),
    () => Task.FromException(new IOException("finish-fixture")), failures.Enqueue);
failed.Query();
await failed.FinishAsync();
Check(failures.Any(x => x.Contains("flush-fixture")) && failures.Any(x => x.Contains("finish-fixture")),
    "session flush and final-save failures are caught and logged");

// All launches below are injected: these tests only connect in-process named-pipe peers.
// No collector executable, ShellExecute, UAC, or unrelated process is launched.
const string adminSid = "S-1-5-32-544";
var filteredAdministrator = new ClaimsIdentity([new Claim(ClaimTypes.DenyOnlySid, adminSid)]);
Check(CollectorClient.CanRequestElevation(filteredAdministrator),
    "UAC-filtered administrator can request elevation using its deny-only administrator SID");
Check(CollectorClient.CanRequestElevation(new ClaimsIdentity([new Claim(ClaimTypes.GroupSid, adminSid)])),
    "enabled administrator group can request elevation");
Check(!CollectorClient.CanRequestElevation(new ClaimsIdentity([new Claim(ClaimTypes.GroupSid, "S-1-5-32-545")])),
    "standard user without administrator membership cannot request same-account elevation");
Check(!CollectorClient.CanRequestElevation(new ClaimsIdentity([new Claim(ClaimTypes.Name, adminSid), new Claim(ClaimTypes.DenyOnlySid, "S-1-5-32-545")])),
    "unrelated claims and unrelated deny-only groups do not grant elevation eligibility");

using (var launcher = new FakeLauncher())
using (var client = new CollectorClient(launcher.ExistingPath, () => CollectorClient.CanRequestElevation(filteredAdministrator), launcher.Launch))
{
    Task ordinary = client.StartAsync();
    await Until(() => client.Connection.Connected, "filtered administrator ordinary connection");
    Task enhanced = client.StartAsync(true);
    await Until(() => client.Connection.Connected && client.Connection.Enhanced, "filtered administrator enhanced connection");
    Check(launcher.Launches == 2 && launcher.Infos[1].UseShellExecute && launcher.Infos[1].Verb == "runas",
        "ordinary collection switches to a UAC launch for a filtered administrator");
    await ordinary.WaitAsync(TimeSpan.FromSeconds(5));
    client.Dispose();
    await enhanced.WaitAsync(TimeSpan.FromSeconds(5));
}

using (var identity = WindowsIdentity.GetCurrent())
{
    if (identity.Claims.Any(claim => claim.Type == ClaimTypes.DenyOnlySid && claim.Value == adminSid))
    {
        using var launcher = new FakeLauncher();
        using var client = new CollectorClient(launcher.ExistingPath, CollectorClient.CanElevateCurrentAccount, launcher.Launch);
        Task run = client.StartAsync(true);
        await Until(() => client.Connection.Connected, "real filtered Windows token reaches the injected UAC launcher");
        Check(launcher.Launches == 1 && launcher.Infos[0].Verb == "runas" && client.Connection.Enhanced,
            "real filtered Windows administrator token passes production eligibility and reaches the launcher");
        client.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

using (var launcher = new FakeLauncher())
using (var client = new CollectorClient(launcher.ExistingPath, () => true, launcher.Launch))
{
    launcher.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task run = client.StartAsync(true);
    await Until(() => launcher.Launches == 1, "first fake launch");
    Task duplicate = client.StartAsync(true);
    Check(duplicate.IsCompleted && launcher.Launches == 1 && client.Connection.Connecting, "double activation while connecting starts only one collector launch");
    launcher.Gate.SetResult();
    await Until(() => client.Connection.Connected, "first enhanced snapshot");
    Check(client.Connection.Enhanced && launcher.Infos[0].Verb == "runas" && launcher.Infos[0].WindowStyle == ProcessWindowStyle.Hidden,
        "enhanced launch configuration is hidden and state follows the received snapshot");
    await client.StartAsync(true);
    await client.StartAsync();
    Check(launcher.Launches == 1 && client.Connection.Connected, "already connected enhanced collector ignores repeated activation");
    await launcher.Peers[0].Send(Snapshots.Create(true, 1.5));
    await Task.Delay(20);
    launcher.Peers[0].Dispose();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!client.Connection.Connected && !client.Connection.Connecting, "pipe disconnect clears collector connection state");
    Task reconnect = client.StartAsync(true);
    await Until(() => client.Connection.Connected && launcher.Launches == 2, "reconnected enhanced snapshot");
    Check(client.Connection.Enhanced, "disconnected collector can reconnect through a fresh launch");
    client.Dispose();
    await reconnect.WaitAsync(TimeSpan.FromSeconds(5));
}

using (var launcher = new FakeLauncher())
using (var client = new CollectorClient(launcher.ExistingPath, () => true,
    (info, token) => info.Verb == "runas" ? Task.FromException<Process?>(new Win32Exception(1223)) : launcher.Launch(info, token)))
{
    Task run = client.StartAsync(true);
    await Until(() => client.Connection.Connected, "ordinary recovery after UAC cancel");
    Check(!client.Connection.Connecting && !client.Connection.Enhanced && client.Connection.Notice!.Contains("已取消") && launcher.Launches == 1,
        "UAC cancellation automatically restores ordinary collection and retains the operation notice");
    await launcher.Peers[0].Send(Snapshots.Create(false));
    Check(client.Connection.Notice is not null, "ordinary snapshots do not dismiss the cancelled UAC notice");
    client.DismissNotice();
    Check(client.Connection.Notice is null && client.Connection.Connected, "explicit dismissal clears only the operation notice");
    client.Dispose();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
}

using (var launcher = new FakeLauncher())
using (var client = new CollectorClient(launcher.ExistingPath, () => false, launcher.Launch))
{
    int snapshots = 0;
    client.SnapshotReceived += _ => Interlocked.Increment(ref snapshots);
    Task ordinary = client.StartAsync();
    await Until(() => client.Connection.Connected, "ordinary connected snapshot");
    await client.StartAsync(true);
    Check(launcher.Launches == 1 && client.Connection.Connected && !client.Connection.Enhanced && client.Connection.Notice!.Contains("另一账户"),
        "cross-account enhancement request preserves the connected ordinary collector");
    await launcher.Peers[0].Send(Snapshots.Create(false, 2));
    await Until(() => Volatile.Read(ref snapshots) == 2, "ordinary stream survives declined enhancement");
    Check(snapshots == 2, "ordinary snapshots continue after unsupported cross-account enhancement");
    Check(client.Connection.Notice is not null, "unsupported-account notice remains visible across ordinary snapshots");
    client.Dispose();
    await ordinary.WaitAsync(TimeSpan.FromSeconds(5));
}

using (var launcher = new FakeLauncher())
using (var client = new CollectorClient(launcher.ExistingPath, () => true, launcher.Launch))
{
    var oldCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseOldCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.SnapshotReceived += snapshot =>
    {
        if (snapshot.IntervalSeconds != 99) return;
        oldCallbackEntered.SetResult();
        releaseOldCallback.Task.GetAwaiter().GetResult();
    };
    Task oldRun = client.StartAsync();
    await Until(() => client.Connection.Connected, "old ordinary snapshot");
    try
    {
        await launcher.Peers[0].Send(Snapshots.Create(false, 99));
        await oldCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task replacement = client.StartAsync(true, forceReconnect: true);
        await Until(() => client.Connection.Connected && client.Connection.Enhanced, "replacement enhanced snapshot");
        releaseOldCallback.SetResult();
        await oldRun.WaitAsync(TimeSpan.FromSeconds(5));
        Check(client.Connection.Connected && client.Connection.Enhanced && launcher.Launches == 2,
            "late old read-loop cleanup cannot overwrite a newer collector connection");
        client.Dispose();
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally { releaseOldCallback.TrySetResult(); }
}

await CollectionRegression.Run(Check);
await ExitOverlayRegression.Run(Check);
OverlayAppearanceRegression.Run(Check);
await ConnectionDetailsTests.Run(Check);
Check(StartupRegistration.BuildCommand(@"C:\Some Folder\WinGlance.App.exe", true) == "\"C:\\Some Folder\\WinGlance.App.exe\" --minimized",
    "silent startup quotes the executable and keeps its argument outside the quotes");
Check(StartupRegistration.ShouldHideOnLaunch(["--minimized"], true) &&
    !StartupRegistration.ShouldHideOnLaunch(["--minimized"], false) &&
    !StartupRegistration.ShouldHideOnLaunch([], true) && !StartupRegistration.ShouldHideOnLaunch(["--minimized-invalid"], true),
    "silent launch requires the exact flag and a working tray; manual launch remains visible");
Console.WriteLine($"Passed {passed} App service regression checks.");

internal static class Snapshots
{
    internal static MonitorSnapshot Create(bool elevated, double interval = 1) => new(DateTimeOffset.UtcNow, interval,
        new(0, 100, 50, 0, 0, 0, 0, 0, "fixture"), [], new Dictionary<string, Capability>(), elevated);
}

internal sealed class FakeLauncher : IDisposable
{
    private int _launches;
    internal int Launches => Volatile.Read(ref _launches);
    internal string ExistingPath => typeof(FakeLauncher).Assembly.Location;
    internal TaskCompletionSource? Gate { get; set; }
    internal List<ProcessStartInfo> Infos { get; } = [];
    internal List<FakePeer> Peers { get; } = [];

    internal async Task<Process?> Launch(ProcessStartInfo info, CancellationToken token)
    {
        Infos.Add(info);
        Interlocked.Increment(ref _launches);
        if (Gate is { } gate) await gate.Task.WaitAsync(token);
        string[] arguments = info.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int index = Array.IndexOf(arguments, "--pipe");
        if (index < 0 || index + 1 >= arguments.Length) throw new InvalidOperationException("missing named pipe");
        var peer = new FakePeer(arguments[index + 1]);
        Peers.Add(peer);
        await peer.Connect(token);
        await peer.Send(Snapshots.Create(info.Verb == "runas"));
        return null;
    }

    public void Dispose() { foreach (var peer in Peers) peer.Dispose(); }
}

internal sealed class FakePeer(string name) : IDisposable
{
    private readonly NamedPipeClientStream _pipe = new(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
    private StreamWriter? _writer;
    private bool _disposed;
    internal async Task Connect(CancellationToken token)
    {
        await _pipe.ConnectAsync(5000, token);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
    }
    internal Task Send(MonitorSnapshot snapshot) => _writer!.WriteLineAsync(JsonSerializer.Serialize(snapshot));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer?.Dispose();
        _pipe.Dispose();
    }
}
