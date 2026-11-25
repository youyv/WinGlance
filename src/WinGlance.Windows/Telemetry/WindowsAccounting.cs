using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

public sealed partial class WindowsSampler
{
    private readonly Guid _collectorRunId = Guid.NewGuid();
    private readonly DateTimeOffset _collectorStartedUtc = DateTimeOffset.UtcNow;
    private readonly long _collectorStartedTimestamp = Stopwatch.GetTimestamp();
    private long _sequence;
    private readonly PhysicalNetworkAccounting _physicalNetwork = new();
    private readonly Dictionary<ProcessIdentity, (SoftwareIdentity Software, long Seen)> _softwareHistory = [];
    private readonly Dictionary<ProcessIdentity, HeldCpu> _cpuHandles = [];
    private DateTimeOffset _previousFrameUtc;
    private long _previousFrameTimestamp;
    private DateTimeOffset _lastCheckUtc;
    private ulong? _lastAwakeTicks;
    private string? _discontinuityReason;
    private long _systemCpuTimestamp;
    private MetricAccounting _systemCpuMetric = AccountingMath.Missing(MetricState.WarmingUp, 0);
    private long? _systemCpuBusyDelta, _systemCpuTotalDelta;
    private long _memoryTimestamp;
    private bool _memoryWasReady;

    private bool CheckAccountingDiscontinuity(double seconds)
    {
        var utc = DateTimeOffset.UtcNow;
        bool first = _lastCheckUtc == default;
        _discontinuityReason = first ? "首次采样，正在建立统计基线" :
            !AccountingMath.Continuous(seconds) ? "采集间隔超过连续统计范围" :
            Math.Abs((utc - _lastCheckUtc).TotalSeconds - seconds) > 2 ? "系统时钟发生跳变" : null;
        if (QueryUnbiasedInterruptTime(out ulong awake))
        {
            if (!first && _lastAwakeTicks is { } previous && (awake < previous ||
                Math.Abs(seconds - (awake - previous) / (double)TimeSpan.TicksPerSecond) > 0.5))
                _discontinuityReason = "系统挂起/恢复，正在重新建立采样基线";
            _lastAwakeTicks = awake;
        }
        else _lastAwakeTicks = null;
        _lastCheckUtc = utc;
        return _discontinuityReason is not null;
    }

    private MetricAccounting CpuMetric(long ticks, double seconds, bool continuous, DateTimeOffset endUtc)
    {
        double integral = ticks / (double)TimeSpan.TicksPerSecond / _processorCount * 100;
        if (!continuous || !AccountingMath.Continuous(seconds)) return AccountingMath.Missing(MetricState.WarmingUp, seconds);
        if (ticks < 0 || integral / seconds > 100.000001)
            return AccountingMath.Missing(MetricState.Error, seconds, "CPU累计时间回退或超出整机容量");
        return new(integral, seconds, integral / seconds, CpuTicksDelta: ticks, IntervalSeconds: seconds,
            StartUtc: endUtc.AddSeconds(-seconds), EndUtc: endUtc);
    }

    private void RememberSoftware(ProcessIdentity identity, SoftwareIdentity software)
    {
        if (identity.StartTimeUtcTicks <= 0) return;
        _softwareHistory[identity] = (software, Stopwatch.GetTimestamp());
    }

    private void TrackCpuHandle(ProcessIdentity identity, long ticks, long timestamp, DateTimeOffset utc)
    {
        if (_cpuHandles.TryGetValue(identity, out var held))
        { held.Ticks = ticks; held.Timestamp = timestamp; held.Utc = utc; return; }
        if (_cpuHandles.Count >= 4096) return;
        var handle = OpenProcess(0x1000, false, identity.Pid);
        if (handle.IsInvalid || !GetProcessTimesAccounting(handle, out long creation, out _, out _, out _) ||
            creation <= 0 || DateTime.FromFileTimeUtc(creation).Ticks != identity.StartTimeUtcTicks)
        { handle.Dispose(); return; }
        _cpuHandles[identity] = new(handle, ticks, timestamp, utc);
    }

    private void CaptureExitedCpu(HashSet<ProcessIdentity> current, Dictionary<ProcessIdentity, MetricAccounting> values, bool reset)
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var pair in _cpuHandles.ToArray())
        {
            if (current.Contains(pair.Key)) continue;
            var held = pair.Value;
            bool readable = GetProcessTimesAccounting(held.Handle, out _, out long exited, out long kernel, out long user);
            if (readable && exited == 0 && (now - held.Timestamp) / (double)Stopwatch.Frequency <= 30) continue;
            if (readable && exited > 0)
            {
                var endUtc = new DateTimeOffset(DateTime.FromFileTimeUtc(exited));
                double seconds = (endUtc - held.Utc).TotalSeconds;
                if (kernel >= 0 && user >= 0 && kernel <= long.MaxValue - user)
                    values[pair.Key] = CpuMetric(kernel + user - held.Ticks, seconds, !reset, endUtc);
            }
            held.Handle.Dispose();
            _cpuHandles.Remove(pair.Key);
        }
    }

    private MetricAccounting ReadMemoryAccounting(bool ready, ulong total, ulong available, bool reset)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = _memoryTimestamp == 0 ? 0 : (now - _memoryTimestamp) / (double)Stopwatch.Frequency;
        _memoryTimestamp = now;
        var metric = AccountingMath.Gauge(ready && available <= total ? total - available : null, seconds,
            !reset && _memoryWasReady, ready ? MetricState.WarmingUp : MetricState.Error);
        _memoryWasReady = ready;
        var endUtc = DateTimeOffset.UtcNow;
        return metric with { StartUtc = endUtc.AddSeconds(-seconds), EndUtc = endUtc };
    }

    private AccountingFrame BuildAccounting(IReadOnlyList<ProcessSample> live, HashSet<ProcessIdentity> previousIdentities,
        Dictionary<ProcessIdentity, MetricAccounting> cpu, TrafficDrain traffic, PdhSnapshot pdh,
        MetricAccounting memory, long? totalMemory, NetworkAccountingSample network, bool reset)
    {
        long now = Stopwatch.GetTimestamp();
        var endUtc = DateTimeOffset.UtcNow;
        double seconds = (now - (_previousFrameTimestamp == 0 ? _collectorStartedTimestamp : _previousFrameTimestamp)) / (double)Stopwatch.Frequency;
        var startUtc = _previousFrameTimestamp == 0 ? _collectorStartedUtc : _previousFrameUtc;
        _previousFrameTimestamp = now;
        _previousFrameUtc = endUtc;
        var pdhAccounting = pdh.Accounting!;
        var samples = live.Where(x => x.Identity.StartTimeUtcTicks > 0).ToDictionary(x => x.Identity);
        var endedInFrame = traffic.Processes.Where(x => x.Value.ExitUtcTicks is { } exited &&
            exited > startUtc.UtcTicks && x.Key.StartTimeUtcTicks < endUtc.UtcTicks).Select(x => x.Key);
        var identities = samples.Keys.Concat(traffic.Bytes.Keys).Concat(cpu.Keys).Concat(endedInFrame).Distinct();
        var result = new List<ProcessAccounting>();
        long orphanDown = 0, orphanUp = 0, orphanRead = 0, orphanWrite = 0, orphanEvents = 0;
        foreach (var identity in identities)
        {
            samples.TryGetValue(identity, out var process);
            traffic.Bytes.TryGetValue(identity, out var bytes);
            SoftwareIdentity? software = process?.Software;
            if (software is null && _softwareHistory.TryGetValue(identity, out var cached)) software = cached.Software;
            if (software is null && traffic.Processes.TryGetValue(identity, out var info))
            {
                try { software = _resolver.Resolve(identity.Pid, info.ImageName, info.Path, -1, identity.StartTimeUtcTicks); }
                catch (Exception ex) when (IsProcessAccessFailure(ex) || ex is IOException) { }
                if (software is not null) RememberSoftware(identity, software);
            }
            if (software is null)
            {
                if (bytes is not null)
                { orphanDown += bytes.Download; orphanUp += bytes.Upload; orphanRead += bytes.DiskRead; orphanWrite += bytes.DiskWrite; orphanEvents += bytes.Events; }
                continue;
            }
            bool gaugeContinuous = !reset && previousIdentities.Contains(identity) && process is not null && pdhAccounting.Continuous;
            long observedEnd = endUtc.UtcTicks;
            if (process is null && cpu.TryGetValue(identity, out var terminalCpu) && terminalCpu.EndUtc is { } exitedUtc)
                observedEnd = Math.Min(observedEnd, exitedUtc.UtcTicks);
            if (traffic.Processes.TryGetValue(identity, out var lifetime) && lifetime.ExitUtcTicks is { } stoppedUtcTicks)
                observedEnd = Math.Min(observedEnd, stoppedUtcTicks);
            long observedStart = Math.Max(startUtc.UtcTicks, identity.StartTimeUtcTicks);
            double observed = reset ? 0 : Math.Min(seconds, Math.Max(0, (observedEnd - observedStart) / (double)TimeSpan.TicksPerSecond));
            var metrics = new Dictionary<StatisticMetric, MetricAccounting>
            {
                [StatisticMetric.Cpu] = cpu.GetValueOrDefault(identity) ?? AccountingMath.Missing(MetricState.WarmingUp, seconds),
                [StatisticMetric.Memory] = AccountingMath.Gauge(process?.PrivateWorkingSetBytes, pdhAccounting.IntervalSeconds, gaugeContinuous),
                [StatisticMetric.DedicatedGpuMemory] = AccountingMath.Gauge(process?.DedicatedGpuBytes, pdhAccounting.IntervalSeconds, gaugeContinuous),
                [StatisticMetric.SharedGpuMemory] = AccountingMath.Gauge(process?.SharedGpuBytes, pdhAccounting.IntervalSeconds, gaugeContinuous)
            };
            var engines = new Dictionary<string, MetricAccounting>(StringComparer.OrdinalIgnoreCase);
            if (process is not null && process.GpuState == MetricState.Ready && gaugeContinuous)
                foreach (var engine in process.GpuEngines)
                    engines[engine.Key] = AccountingMath.Gauge(engine.Value, pdhAccounting.IntervalSeconds, true) with
                    { StartUtc = pdhAccounting.StartUtc, EndUtc = pdhAccounting.EndUtc };
            double? gpu = process?.GpuState == MetricState.Ready ? process.GpuEngines.Values.DefaultIfEmpty(0).Max() : null;
            metrics[StatisticMetric.Gpu] = AccountingMath.Gauge(gpu, pdhAccounting.IntervalSeconds, gaugeContinuous,
                process?.GpuState ?? MetricState.WarmingUp);
            foreach (var key in new[] { StatisticMetric.Memory, StatisticMetric.DedicatedGpuMemory, StatisticMetric.SharedGpuMemory, StatisticMetric.Gpu })
                metrics[key] = metrics[key] with { StartUtc = pdhAccounting.StartUtc, EndUtc = pdhAccounting.EndUtc };
            bool identityCovered = previousIdentities.Contains(identity) ||
                traffic.Processes.ContainsKey(identity) && identity.StartTimeUtcTicks >= traffic.StartUtc.UtcTicks;
            var trafficStart = identity.StartTimeUtcTicks > traffic.StartUtc.UtcTicks
                ? new DateTimeOffset(identity.StartTimeUtcTicks, TimeSpan.Zero) : traffic.StartUtc;
            var trafficEnd = traffic.EndUtc;
            if (lifetime?.ExitUtcTicks is { } trafficExit && trafficExit < trafficEnd.UtcTicks)
                trafficEnd = new DateTimeOffset(trafficExit, TimeSpan.Zero);
            else if (process is null && cpu.TryGetValue(identity, out var finalCpu) && finalCpu.EndUtc is { } cpuEnd && cpuEnd < trafficEnd)
                trafficEnd = cpuEnd;
            double trafficSeconds = Math.Max(0, traffic.IntervalSeconds - (trafficStart - traffic.StartUtc).TotalSeconds -
                (traffic.EndUtc - trafficEnd).TotalSeconds);
            bool trafficReady = !reset && traffic.Quality.State == MetricState.Ready && identityCovered &&
                AccountingMath.Continuous(trafficSeconds);
            var trafficState = traffic.Quality.State == MetricState.Ready && !identityCovered
                ? MetricState.WarmingUp : traffic.Quality.State;
            if (!trafficReady && bytes is not null)
            {
                // Known event ownership alone cannot establish a complete observation window.
                // Preserve these received bytes in the quality totals instead of dropping them.
                orphanDown += bytes.Download; orphanUp += bytes.Upload;
                orphanRead += bytes.DiskRead; orphanWrite += bytes.DiskWrite; orphanEvents += bytes.Events;
            }
            // An observed identity without events is a measured zero; unseen identities never enter this loop.
            metrics[StatisticMetric.Download] = AccountingMath.Bytes(bytes?.Download ?? 0, trafficSeconds, trafficReady, trafficState);
            metrics[StatisticMetric.Upload] = AccountingMath.Bytes(bytes?.Upload ?? 0, trafficSeconds, trafficReady, trafficState);
            metrics[StatisticMetric.DiskRead] = AccountingMath.Bytes(bytes?.DiskRead ?? 0, trafficSeconds, trafficReady, trafficState);
            metrics[StatisticMetric.DiskWrite] = AccountingMath.Bytes(bytes?.DiskWrite ?? 0, trafficSeconds, trafficReady, trafficState);
            foreach (var key in new[] { StatisticMetric.Download, StatisticMetric.Upload, StatisticMetric.DiskRead, StatisticMetric.DiskWrite })
                metrics[key] = metrics[key] with { StartUtc = trafficStart, EndUtc = trafficEnd };
            result.Add(new(identity, software, observed, metrics, engines,
                observed > 0 ? new DateTimeOffset(observedStart, TimeSpan.Zero) : null,
                observed > 0 ? new DateTimeOffset(observedEnd, TimeSpan.Zero) : null));
        }
        var etw = traffic.Quality with
        {
            UnattributedDownloadBytes = traffic.Quality.UnattributedDownloadBytes + orphanDown,
            UnattributedUploadBytes = traffic.Quality.UnattributedUploadBytes + orphanUp,
            UnattributedDiskReadBytes = traffic.Quality.UnattributedDiskReadBytes + orphanRead,
            UnattributedDiskWriteBytes = traffic.Quality.UnattributedDiskWriteBytes + orphanWrite,
            UnattributedEvents = traffic.Quality.UnattributedEvents + orphanEvents,
            Detail = orphanEvents > 0 ? traffic.Quality.Detail + "；未归属统计含身份元数据缺失或有效采集窗口不完整的已接收字节" : traffic.Quality.Detail
        };
        var system = new Dictionary<StatisticMetric, MetricAccounting>
        {
            [StatisticMetric.Cpu] = _systemCpuMetric,
            [StatisticMetric.Memory] = memory,
            [StatisticMetric.Gpu] = pdhAccounting.Gpu,
            [StatisticMetric.DedicatedGpuMemory] = pdhAccounting.DedicatedGpuMemory,
            [StatisticMetric.SharedGpuMemory] = pdhAccounting.SharedGpuMemory,
            [StatisticMetric.Download] = network.Download,
            [StatisticMetric.Upload] = network.Upload,
            [StatisticMetric.DiskRead] = pdhAccounting.DiskRead,
            [StatisticMetric.DiskWrite] = pdhAccounting.DiskWrite
        };
        long cutoff = now - 30 * Stopwatch.Frequency;
        foreach (var key in _softwareHistory.Where(x => x.Value.Seen < cutoff).Select(x => x.Key).ToArray()) _softwareHistory.Remove(key);
        if (_softwareHistory.Count > 32768)
            foreach (var key in _softwareHistory.OrderBy(x => x.Value.Seen).Take(_softwareHistory.Count - 32768).Select(x => x.Key).ToArray()) _softwareHistory.Remove(key);
        return new(_collectorRunId, ++_sequence, startUtc, endUtc, seconds, result,
            new(system, network.Scope, network.Interfaces, pdhAccounting.Adapters, etw,
                _systemCpuBusyDelta, _systemCpuTotalDelta, _processorCount, totalMemory, pdhAccounting.DedicatedCapacityBytes),
            reset, _discontinuityReason);
    }

    private sealed class HeldCpu(SafeProcessHandle handle, long ticks, long timestamp, DateTimeOffset utc)
    {
        internal SafeProcessHandle Handle = handle;
        internal long Ticks = ticks;
        internal long Timestamp = timestamp;
        internal DateTimeOffset Utc = utc;
    }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryUnbiasedInterruptTime(out ulong time);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimesAccounting(SafeProcessHandle process,
        out long creation, out long exit, out long kernel, out long user);
}
