using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

/// <summary>Single-threaded, once-per-second snapshot sampler. All rates use elapsed monotonic time.</summary>
public sealed partial class WindowsSampler : IDisposable
{
    private readonly IProcessIdentityResolver _resolver;
    private readonly PdhMetrics _pdh = new();
    private readonly EtwTrafficCollector _traffic;
    private readonly bool _elevated;
    private readonly int _processorCount;
    private Dictionary<ProcessIdentity, (long CpuTicks, long SampleTimestamp)> _cpuTimes = [];
    private HashSet<ProcessIdentity> _knownIdentities = [];
    private long _lastTimestamp;
    private (ulong Idle, ulong Kernel, ulong User)? _systemTimes;
    private string? _selectedNetwork;
    private bool _disposed;

    public WindowsSampler(IProcessIdentityResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        using var identity = WindowsIdentity.GetCurrent();
        _elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        _processorCount = (int)Math.Max(1, GetActiveProcessorCount(0xFFFF));
        _traffic = new EtwTrafficCollector(_elevated);
    }

    public MonitorSnapshot Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long now = Stopwatch.GetTimestamp();
        double interval = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        bool hasInterval = interval > 0.001;
        bool resetAccounting = CheckAccountingDiscontinuity(interval);
        var drained = _traffic.Drain(resetAccounting);
        var traffic = drained.Bytes;
        var trafficCapability = drained.Capability;
        var pdh = _pdh.Sample(resetAccounting);
        var cpuAccounting = new Dictionary<ProcessIdentity, MetricAccounting>();
        var previousIdentities = _knownIdentities;
        var newCpuTimes = new Dictionary<ProcessIdentity, (long CpuTicks, long SampleTimestamp)>();
        var processes = new List<ProcessSample>();
        int inaccessible = 0;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int pid;
                string name;
                try { pid = process.Id; name = process.ProcessName; }
                catch (Exception ex) when (IsProcessAccessFailure(ex)) { continue; }
                if (pid == 0) continue; // Idle is represented by the system CPU percentage.
                long started = 0;
                long? cpuTicks = null;
                int session = -1;
                try { started = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception ex) when (IsProcessAccessFailure(ex)) { inaccessible++; }
                try { cpuTicks = process.TotalProcessorTime.Ticks; }
                catch (Exception ex) when (IsProcessAccessFailure(ex)) { }
                long cpuTimestamp = Stopwatch.GetTimestamp();
                var cpuUtc = DateTimeOffset.UtcNow;
                try { session = process.SessionId; }
                catch (Exception ex) when (IsProcessAccessFailure(ex)) { }
                string? path = ReadExecutablePath(pid);
                var processIdentity = new ProcessIdentity(pid, started);
                double? cpu = null;
                if (started != 0 && cpuTicks.HasValue)
                {
                    if (_cpuTimes.TryGetValue(processIdentity, out var previous))
                    {
                        double cpuInterval = (cpuTimestamp - previous.SampleTimestamp) / (double)Stopwatch.Frequency;
                        if (cpuInterval > 0.001)
                            cpu = Math.Clamp((cpuTicks.Value - previous.CpuTicks) / (cpuInterval * TimeSpan.TicksPerSecond * _processorCount) * 100, 0, 100);
                        cpuAccounting[processIdentity] = CpuMetric(cpuTicks.Value - previous.CpuTicks, cpuInterval,
                            !resetAccounting, cpuUtc);
                    }
                    newCpuTimes[processIdentity] = (cpuTicks.Value, cpuTimestamp);
                }
                // A failed creation-time read must not turn a reused PID into a stable identity.
                bool completeTraffic = hasInterval && started > 0 && trafficCapability.State == MetricState.Ready && _knownIdentities.Contains(processIdentity);
                traffic.TryGetValue(processIdentity, out var bytes);
                double? Rate(long? value) => completeTraffic ? (value ?? 0) / interval : null;
                SoftwareIdentity software;
                try { software = _resolver.Resolve(pid, name, path, session, started); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
                {
                    software = new SoftwareIdentity(path ?? $"process:{name}", name, name, path, null,
                        SoftwareCategory.Unknown, "无法读取软件身份信息");
                }
                RememberSoftware(processIdentity, software);
                // Ensure a process which exited while metadata was resolving is not confused with a recycled PID.
                try { if (process.HasExited) continue; }
                catch (Exception ex) when (IsProcessAccessFailure(ex)) { }
                // Commit the retained baseline only for a live row. If metadata resolution races
                // process exit, CaptureExitedCpu must still measure from the previous sample.
                if (started != 0 && cpuTicks.HasValue)
                    TrackCpuHandle(processIdentity, cpuTicks.Value, cpuTimestamp, cpuUtc);
                processes.Add(new ProcessSample(processIdentity, software, cpu,
                    pdh.WorkingSet.TryGetValue(pid, out var ws) ? ws : null,
                    Rate(bytes?.Download), Rate(bytes?.Upload), Rate(bytes?.DiskRead), Rate(bytes?.DiskWrite),
                    pdh.Engines.TryGetValue(pid, out var engines) ? engines : new Dictionary<string, double>(),
                    pdh.DedicatedAvailable && !pdh.InvalidDedicatedPids.Contains(pid) ? pdh.Dedicated.GetValueOrDefault(pid) : null,
                    pdh.SharedAvailable && !pdh.InvalidSharedPids.Contains(pid) ? pdh.Shared.GetValueOrDefault(pid) : null,
                    pdh.InvalidGpuPids.Contains(pid) ? MetricState.Error : pdh.GpuCapability.State));
            }
        }
        CaptureExitedCpu(processes.Select(p => p.Identity).ToHashSet(), cpuAccounting, resetAccounting);
        _cpuTimes = newCpuTimes;
        _knownIdentities = processes.Select(p => p.Identity).ToHashSet();
        _traffic.SetIdentities(processes.Select(p => p.Identity));

        double? systemCpu = ReadSystemCpu(resetAccounting);
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        bool memoryReady = GlobalMemoryStatusEx(ref memory);
        var memoryAccounting = ReadMemoryAccounting(memoryReady, memory.TotalPhysical, memory.AvailablePhysical, resetAccounting);
        var networkAccounting = _physicalNetwork.Sample(resetAccounting);
        var network = SampleNetwork(networkAccounting);
        var capabilities = new Dictionary<string, Capability>
        {
            ["cpu"] = new(hasInterval ? MetricState.Ready : MetricState.WarmingUp,
                hasInterval ? $"CPU 按 {_processorCount} 个逻辑处理器归一化；{inaccessible} 个受保护进程信息不可读" +
                    (_processorCount > 64 ? "；整机 GetSystemTimes 跨处理器组未支持，历史系统 CPU 不记为完整" : "") : "正在建立 CPU 采样基线"),
            ["memory"] = new(pdh.WorkingSetSupported && memoryReady ? MetricState.Ready : MetricState.Unsupported,
                pdh.UsingProcessV2 ? "内存为专用工作集；Process V2 唯一实例按 PID 对应" :
                "内存为专用工作集；旧版 Process 重名实例无法唯一对应时显示 —"),
            ["network"] = !hasInterval && trafficCapability.State == MetricState.Ready
                ? new(MetricState.WarmingUp, "正在建立 ETW 网络采样基线") : trafficCapability,
            ["disk"] = !hasInterval && trafficCapability.State == MetricState.Ready
                ? new(MetricState.WarmingUp, "正在建立 ETW 磁盘采样基线") : trafficCapability,
            ["gpu"] = pdh.GpuCapability,
            ["gpuMemory"] = pdh.GpuMemoryCapability,
            ["systemNetwork"] = network.Capability,
            ["systemDisk"] = new(pdh.DiskRead.HasValue ? MetricState.Ready : hasInterval ? MetricState.Unsupported : MetricState.WarmingUp,
                "全局磁盘为 PhysicalDisk 总吞吐；进程归属依据 ETW 磁盘完成事件")
        };
        return new MonitorSnapshot(DateTimeOffset.Now, interval,
            new SystemSample(systemCpu, memoryReady ? (long)memory.TotalPhysical : 0,
                memoryReady ? (long)memory.AvailablePhysical : 0, network.Download, network.Upload,
                pdh.DiskRead, pdh.DiskWrite, pdh.GpuPercent, network.Name)
            {
                NetworkInterfaces = network.Interfaces,
                DefaultNetworkInterfaceId = network.Id,
                PhysicalDisks = pdh.Accounting?.PhysicalDisks ?? []
            }, processes, capabilities, _elevated)
        {
            Accounting = BuildAccounting(processes, previousIdentities, cpuAccounting, drained, pdh,
                memoryAccounting, memoryReady ? (long)memory.TotalPhysical : null, networkAccounting, resetAccounting)
        };
    }

    private NetworkSample SampleNetwork(NetworkAccountingSample sampled)
    {
        var interfaces = sampled.Devices;
        var candidates = interfaces.Where(x => x.IsConnected).ToList();
        var physicalCandidates = candidates.Where(x => x.IsPhysical).ToList();
        var eligible = physicalCandidates.Count > 0 ? physicalCandidates : candidates;
        if (eligible.Count == 0)
            return new NetworkSample(null, null, "无活动网络",
                new(sampled.Download.State == MetricState.Error ? MetricState.Error : MetricState.Unsupported,
                    sampled.Download.State == MetricState.Error ? "无法读取网络接口计数器" : "没有活动网络接口"), interfaces);
        var best = eligible.OrderByDescending(x => (x.DownloadBytesPerSecond ?? 0) + (x.UploadBytesPerSecond ?? 0)).First();
        var old = eligible.FirstOrDefault(x => x.Id == _selectedNetwork);
        // Small traffic variations should not make the selected interface oscillate every second.
        if (old is not null && (best.DownloadBytesPerSecond ?? 0) + (best.UploadBytesPerSecond ?? 0) <
            Math.Max(4096, ((old.DownloadBytesPerSecond ?? 0) + (old.UploadBytesPerSecond ?? 0)) * 1.25)) best = old;
        _selectedNetwork = best.Id;
        return new NetworkSample(best.DownloadBytesPerSecond, best.UploadBytesPerSecond, best.Name,
            new(best.State, $"单接口：{best.Detail}；优先活动物理网卡，不叠加 VPN/虚拟网卡"), interfaces, best.Id);
    }

    private double? ReadSystemCpu(bool resetAccounting)
    {
        long timestamp = Stopwatch.GetTimestamp();
        double seconds = _systemCpuTimestamp == 0 ? 0 : (timestamp - _systemCpuTimestamp) / (double)Stopwatch.Frequency;
        _systemCpuTimestamp = timestamp;
        _systemCpuBusyDelta = _systemCpuTotalDelta = null;
        _systemCpuMetric = AccountingMath.Missing(MetricState.WarmingUp, seconds);
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        { _systemTimes = null; _systemCpuMetric = AccountingMath.Missing(MetricState.Error, seconds); return null; }
        var previous = _systemTimes;
        _systemTimes = (idle, kernel, user);
        if (previous is null) return null;
        if (idle < previous.Value.Idle || kernel < previous.Value.Kernel || user < previous.Value.User) return null;
        ulong total = kernel - previous.Value.Kernel + user - previous.Value.User;
        ulong idleDelta = idle - previous.Value.Idle;
        double? percent = total == 0 ? null : Math.Clamp((total - Math.Min(idleDelta, total)) * 100d / total, 0, 100);
        if (_processorCount > 64)
            _systemCpuMetric = AccountingMath.Missing(MetricState.Unsupported, seconds, "超过64逻辑处理器：GetSystemTimes仅覆盖调用线程处理器组");
        else if (!resetAccounting && total <= long.MaxValue && idleDelta <= total)
        {
            _systemCpuMetric = AccountingMath.Gauge(percent, seconds, AccountingMath.Continuous(seconds));
            if (_systemCpuMetric.State == MetricState.Ready)
            {
                _systemCpuBusyDelta = (long)(total - idleDelta);
                _systemCpuTotalDelta = (long)total;
                var endUtc = DateTimeOffset.UtcNow;
                _systemCpuMetric = _systemCpuMetric with
                { CpuTicksDelta = _systemCpuBusyDelta, StartUtc = endUtc.AddSeconds(-seconds), EndUtc = endUtc };
            }
        }
        return percent;
    }

    private static string? ReadExecutablePath(int pid)
    {
        using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return null;
        var path = new StringBuilder(1024);
        int size = path.Capacity;
        if (QueryFullProcessImageName(handle, 0, path, ref size)) return path.ToString();
        if (Marshal.GetLastWin32Error() != 122) return null; // ERROR_INSUFFICIENT_BUFFER
        path.EnsureCapacity(32768);
        size = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
    }

    private static bool IsProcessAccessFailure(Exception ex) => ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _traffic.Dispose();
        _pdh.Dispose();
        foreach (var held in _cpuHandles.Values) held.Handle.Dispose();
        _cpuHandles.Clear();
    }

    private sealed record NetworkSample(double? Download, double? Upload, string Name, Capability Capability,
        IReadOnlyList<NetworkInterfaceSample> Interfaces, string? Id = null);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
