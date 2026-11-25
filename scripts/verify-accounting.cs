#:property TargetFramework=net10.0-windows10.0.19041.0
#:property PublishAot=false
#:project ../src/WinGlance.Windows/WinGlance.Windows.csproj

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using WinGlance.Core;
using WinGlance.Windows.Telemetry;

// Standalone ordinary-permission validation. Does not launch the UI, elevate, or alter system settings.
if (args.Contains("--worker"))
{
    var work = Stopwatch.StartNew();
    while (work.Elapsed < TimeSpan.FromSeconds(1.5)) Thread.SpinWait(10000);
    return;
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

var resolver = new Resolver();
using var sampler = new WindowsSampler(resolver);
var first = sampler.Sample();
Check(first.Accounting is { Discontinuity: true, Sequence: 1 }, "first sample establishes an accounting baseline");
await Task.Delay(1100);
var second = sampler.Sample();
var accounting = second.Accounting!;
Check(accounting.CollectorRunId == first.Accounting!.CollectorRunId && accounting.Sequence == 2, "collector identity and sequence are stable");
Check(accounting.System!.Metrics[StatisticMetric.Cpu].CpuTicksDelta.HasValue, "system CPU has raw busy ticks");
Check(accounting.Processes.Any(p => p.Metrics[StatisticMetric.Cpu].CpuTicksDelta.HasValue), "process CPU has raw ticks");
if (accounting.System.Networks is { Count: > 0 } interfaces && interfaces.All(x => x.State == MetricState.Ready))
{
    Check(accounting.System.Metrics[StatisticMetric.Download].BytesDelta == interfaces.Sum(x => x.DownloadBytesDelta), "physical-network download equals integer interface deltas");
    Check(accounting.System.Metrics[StatisticMetric.Upload].BytesDelta == interfaces.Sum(x => x.UploadBytesDelta), "physical-network upload equals integer interface deltas");
}
if (accounting.System.Metrics[StatisticMetric.DiskRead].State == MetricState.Ready)
    Check(accounting.System.Metrics[StatisticMetric.DiskRead].BytesDelta is >= 0, "disk accounting uses a nonnegative raw integer delta");
if (!second.IsElevated)
    Check(accounting.Processes.All(p => p.Metrics[StatisticMetric.Download].State == MetricState.PermissionRequired &&
        p.Metrics[StatisticMetric.Download].BytesDelta is null), "ordinary permission never becomes fake application traffic zero");

var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    childInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
childInfo.ArgumentList.Add("--worker");
using (var child = Process.Start(childInfo)!)
{
    await Task.Delay(150);
    var during = sampler.Sample();
    Check(during.Processes.Any(x => x.Identity.Pid == child.Id), "controlled worker observed before exit");
    await child.WaitForExitAsync();
    var after = sampler.Sample();
    var terminal = after.Accounting!.Processes.SingleOrDefault(x => x.Identity.Pid == child.Id);
    Check(!after.Processes.Any(x => x.Identity.Pid == child.Id) && terminal?.Metrics[StatisticMetric.Cpu].CpuTicksDelta is > 0,
        "exited worker CPU tail survives outside the live-process list");
    var exited = new DateTimeOffset(child.ExitTime.ToUniversalTime());
    Check(terminal!.ObservedEndUtc == exited && terminal.ObservedStartUtc >= after.Accounting.StartUtc &&
        terminal.Metrics[StatisticMetric.Cpu].EndUtc == exited,
        "exited worker observation and terminal CPU end at its actual exit time");
    await Task.Delay(150);
    Check(!sampler.Sample().Accounting!.Processes.Any(x => x.Identity == terminal.Identity),
        "terminal CPU is emitted exactly once");
}

using (var child = Process.Start(childInfo)!)
{
    await Task.Delay(150);
    long beforeBaseline = child.TotalProcessorTime.Ticks;
    var during = sampler.Sample();
    long afterBaseline = child.TotalProcessorTime.Ticks;
    Check(during.Processes.Any(x => x.Identity.Pid == child.Id), "metadata-race worker has an observed CPU baseline");
    await Task.Delay(400);
    resolver.OnResolve = pid => { if (pid == child.Id) child.WaitForExit(5000); };
    var after = sampler.Sample();
    resolver.OnResolve = null;
    long finalTicks = child.TotalProcessorTime.Ticks;
    var terminal = after.Accounting!.Processes.Single(x => x.Identity.Pid == child.Id);
    long? recorded = terminal.Metrics[StatisticMetric.Cpu].CpuTicksDelta;
    Check(recorded >= finalTicks - afterBaseline && recorded <= finalTicks - beforeBaseline,
        "exit during metadata resolution preserves the entire CPU delta from the previous baseline");
}

await Task.Delay(5500);
var gap = sampler.Sample().Accounting!;
Check(gap.Discontinuity && gap.System!.Metrics.Values.All(x => x.ValidSeconds == 0), "long sampling gap does not integrate stale gauges or counters");
await Task.Delay(1100);
var resumed = sampler.Sample().Accounting!;
Check(!resumed.Discontinuity && resumed.System!.Metrics[StatisticMetric.Cpu].State == MetricState.Ready, "sampling resumes from new baselines");
using var replacement = new WindowsSampler(new Resolver());
Check(replacement.Sample().Accounting!.CollectorRunId != resumed.CollectorRunId, "reconnection gets a new collector identity");
Console.WriteLine(JsonSerializer.Serialize(new { resumed.System, ProcessCount = resumed.Processes.Count }));

sealed class Resolver : IProcessIdentityResolver
{
    public Action<int>? OnResolve { get; set; }
    public SoftwareIdentity Resolve(int pid, string processName, string? filePath, int sessionId, long startTimeUtcTicks = 0)
    {
        OnResolve?.Invoke(pid);
        return new(filePath ?? $"process:{pid}:{startTimeUtcTicks}", processName, processName, filePath, null, SoftwareCategory.Unknown, "accounting smoke");
    }
}
