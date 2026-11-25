using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

/// <summary>Transport byte accounting and physical disk completion events, never Process I/O.</summary>
internal sealed class EtwTrafficCollector : IDisposable
{
    private readonly object _gate = new();
    private readonly ProcessLifetimeMap _lifetimes = new();
    private Dictionary<ProcessIdentity, TrafficBytes> _pending = [];
    private TraceEventSession? _session;
    private Task? _pump;
    private Capability _capability;
    private int _lastLost;
    private long _unattributed;
    private long _unattributedEvents;
    private long _totalUnattributed;
    private long _totalUnattributedEvents;
    private TrafficBytes _unattributedBytes = new();
    private readonly Dictionary<ProcessIdentity, TrafficProcessInfo> _processInfo = [];
    private long _lastDrainTimestamp;
    private DateTimeOffset _lastDrainUtc;
    private bool _disposed;
    private const int MaxProcessBuckets = 32768;

    public EtwTrafficCollector(bool elevated)
    {
        _capability = new Capability(MetricState.PermissionRequired, "进程网络与磁盘需要管理员采集器");
        if (!elevated) return;
        try
        {
            // Never attach to, restart or stop another application's kernel session.
            _session = new TraceEventSession($"WinGlance-{Environment.ProcessId}-{Guid.NewGuid():N}",
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
            { StopOnDispose = true, BufferSizeMB = 8, BufferQuantumKB = 64 };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit |
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
            // Only the thread/PID map is needed; do not accumulate filename or disk latency histories.
            var kernel = new KernelTraceEventParser(_session.Source,
                KernelTraceEventParser.ParserTrackingOptions.ThreadToProcess);

            kernel.ProcessStart += e => ProcessStarted(e.ProcessID, e.TimeStamp.ToUniversalTime().Ticks, e.UniqueProcessKey, e.ImageFileName);
            kernel.ProcessStop += e => ProcessStopped(e.ProcessID, e.TimeStamp.ToUniversalTime().Ticks, e.UniqueProcessKey, e.ImageFileName);

            // TraceEvent 3.1.21 FixupData replaces ProcessID from the TCP/UDP payload PID.
            // It is not the raw ETW header PID (which is often the kernel worker).
            kernel.TcpIpRecv += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Download);
            kernel.TcpIpSend += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Upload);
            kernel.UdpIpRecv += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Download);
            kernel.UdpIpSend += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Upload);
            kernel.TcpIpRecvIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Download);
            kernel.TcpIpSendIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Upload);
            kernel.UdpIpRecvIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Download);
            kernel.UdpIpSendIPV6 += e => Add(e.ProcessID, e.TimeStamp, e.size, TrafficKind.Upload);
            // DiskIOTraceData fixes up the issuing thread through the process/thread event map.
            // Deferred writes may legitimately be charged to System; unknown ownership is discarded.
            kernel.DiskIORead += e => Add(e.ProcessID, e.TimeStamp, e.TransferSize, TrafficKind.DiskRead);
            kernel.DiskIOWrite += e => Add(e.ProcessID, e.TimeStamp, e.TransferSize, TrafficKind.DiskWrite);
            _capability = new Capability(MetricState.Ready, "ETW TCP/UDP 应用字节；磁盘为完成事件，缓存写回可能归属 System");
            var session = _session;
            _pump = Task.Factory.StartNew(() =>
            {
                try
                {
                    session.Source.Process();
                    lock (_gate)
                        if (!_disposed) _capability = new Capability(MetricState.Error, "ETW 会话已意外结束");
                }
                catch (Exception ex)
                {
                    lock (_gate)
                        if (!_disposed) _capability = new Capability(MetricState.Error, $"ETW 已停止：{ex.Message}");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            _capability = new Capability(MetricState.Error, $"ETW 无法启动：{ex.Message}");
        }
    }

    public void SetIdentities(IEnumerable<ProcessIdentity> identities)
    {
        lock (_gate) _lifetimes.ObserveSnapshot(identities.Take(MaxProcessBuckets), DateTime.UtcNow.Ticks);
    }

    private void ProcessStarted(int pid, long eventTicks, ulong processKey, string imageName)
    {
        ProcessIdentity? identity = null;
        string? path = null;
        // One limited-information open per new process, never one per packet.
        using (var handle = OpenProcess(0x1000, false, pid))
            if (!handle.IsInvalid && GetProcessTimes(handle, out long creation, out _, out _, out _) && creation > 0)
            {
                long ticks = DateTime.FromFileTimeUtc(creation).Ticks;
                if (ticks <= eventTicks)
                {
                    identity = new ProcessIdentity(pid, ticks);
                    var text = new StringBuilder(32768);
                    int length = text.Capacity;
                    if (QueryFullProcessImageName(handle, 0, text, ref length)) path = text.ToString();
                }
            }
        lock (_gate)
        {
            _lifetimes.ObserveStart(pid, eventTicks, processKey, identity);
            if (identity is not null && _processInfo.Count < MaxProcessBuckets)
                _processInfo[identity] = new(identity, imageName, path, eventTicks);
            // A start event may arrive after network buffers. Never retain a pre-fence bucket under
            // a different generation of this PID; conservatively count it as unassigned instead.
            foreach (var key in _pending.Keys.Where(x => x.Pid == pid && x != identity).ToArray())
            {
                var bytes = _pending[key];
                Unattributed(bytes);
                _pending.Remove(key);
            }
        }
    }

    private void Add(int pid, DateTime time, int size, TrafficKind kind)
    {
        if (size <= 0) return;
        lock (_gate)
        {
            var identity = _lifetimes.Resolve(pid, time.ToUniversalTime().Ticks);
            if (identity is null) { Unattributed(size, kind); return; }
            if (!_pending.TryGetValue(identity, out var bytes))
            {
                if (_pending.Count >= MaxProcessBuckets) { Unattributed(size, kind); return; }
                _pending.Add(identity, bytes = new TrafficBytes());
            }
            bytes.Events++;
            switch (kind)
            {
                case TrafficKind.Download: bytes.Download += size; break;
                case TrafficKind.Upload: bytes.Upload += size; break;
                case TrafficKind.DiskRead: bytes.DiskRead += size; break;
                case TrafficKind.DiskWrite: bytes.DiskWrite += size; break;
            }
        }
    }

    private void ProcessStopped(int pid, long eventTicks, ulong processKey, string imageName)
    {
        lock (_gate)
        {
            var identity = _lifetimes.ObserveStop(pid, eventTicks, processKey);
            if (identity is null) return;
            if (_processInfo.TryGetValue(identity, out var info))
                _processInfo[identity] = info with { ExitUtcTicks = Math.Min(info.ExitUtcTicks ?? long.MaxValue, eventTicks) };
            else if (_processInfo.Count < MaxProcessBuckets)
                _processInfo[identity] = new(identity, imageName, null, identity.StartTimeUtcTicks, eventTicks);
        }
    }

    private void Unattributed(long size, TrafficKind kind)
    {
        switch (kind)
        {
            case TrafficKind.Download: _unattributedBytes.Download += size; break;
            case TrafficKind.Upload: _unattributedBytes.Upload += size; break;
            case TrafficKind.DiskRead: _unattributedBytes.DiskRead += size; break;
            case TrafficKind.DiskWrite: _unattributedBytes.DiskWrite += size; break;
        }
        UnattributedTotal(size, 1);
    }

    private void Unattributed(TrafficBytes bytes)
    {
        _unattributedBytes.Download += bytes.Download;
        _unattributedBytes.Upload += bytes.Upload;
        _unattributedBytes.DiskRead += bytes.DiskRead;
        _unattributedBytes.DiskWrite += bytes.DiskWrite;
        UnattributedTotal(bytes.Download + bytes.Upload + bytes.DiskRead + bytes.DiskWrite, bytes.Events);
    }

    private void UnattributedTotal(long bytes, long events)
    {
        _unattributed += bytes;
        _unattributedEvents += events;
        _totalUnattributed += bytes;
        _totalUnattributedEvents += events;
    }

    public TrafficDrain Drain(bool reset = false)
    {
        lock (_gate)
        {
            var result = _pending;
            _pending = [];
            long timestamp = Stopwatch.GetTimestamp();
            var endUtc = DateTimeOffset.UtcNow;
            double seconds = _lastDrainTimestamp == 0 ? 0 : (timestamp - _lastDrainTimestamp) / (double)Stopwatch.Frequency;
            var startUtc = _lastDrainTimestamp == 0 ? endUtc : _lastDrainUtc;
            _lastDrainTimestamp = timestamp;
            _lastDrainUtc = endUtc;
            var capability = _capability;
            long lostDelta = 0;
            try
            {
                if (_session is not null && capability.State == MetricState.Ready)
                {
                    int lost = _session.EventsLost;
                    if (lost > _lastLost)
                    {
                        lostDelta = lost - _lastLost;
                        capability = new Capability(MetricState.Error, $"ETW 本窗口丢失 {lost - _lastLost} 个事件，速率暂不可用");
                        _lifetimes.Invalidate();
                    }
                    _lastLost = lost;
                }
            }
            catch (Exception ex) { capability = new Capability(MetricState.Error, $"ETW 状态不可用：{ex.Message}"); }
            if (_totalUnattributedEvents > 0)
                capability = capability with { Message = capability.Message +
                    $"；未归属 {_unattributedEvents} 事件/{_unattributed} B（累计 {_totalUnattributedEvents} 事件/{_totalUnattributed} B）" };
            var state = capability.State;
            if (state == MetricState.Ready && (reset || !AccountingMath.Continuous(seconds))) state = MetricState.WarmingUp;
            var quality = new EtwQualityAccounting(_unattributedBytes.Download, _unattributedBytes.Upload,
                _unattributedBytes.DiskRead, _unattributedBytes.DiskWrite, _unattributedEvents,
                lostDelta, seconds, state, capability.Message);
            var info = _processInfo.ToDictionary(x => x.Key, x => x.Value);
            long cutoff = endUtc.UtcTicks - TimeSpan.FromSeconds(30).Ticks;
            foreach (var key in _processInfo.Where(x => Math.Max(x.Value.EventUtcTicks, x.Value.ExitUtcTicks ?? 0) < cutoff).Select(x => x.Key).ToArray()) _processInfo.Remove(key);
            if (reset) _lifetimes.Invalidate();
            _unattributed = 0;
            _unattributedEvents = 0;
            _unattributedBytes = new();
            return new(result, capability, seconds, startUtc, endUtc, quality, info);
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        // Stop only this unique session. Disposing unblocks the dedicated Process() thread.
        try { _session?.Dispose(); } catch { /* shutdown is best effort */ }
        _session = null;
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private enum TrafficKind { Download, Upload, DiskRead, DiskWrite }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}

internal sealed record TrafficProcessInfo(ProcessIdentity Identity, string ImageName, string? Path, long EventUtcTicks,
    long? ExitUtcTicks = null);
internal sealed record TrafficDrain(Dictionary<ProcessIdentity, TrafficBytes> Bytes, Capability Capability,
    double IntervalSeconds, DateTimeOffset StartUtc, DateTimeOffset EndUtc, EtwQualityAccounting Quality,
    IReadOnlyDictionary<ProcessIdentity, TrafficProcessInfo> Processes);

internal sealed class TrafficBytes
{
    public long Download;
    public long Upload;
    public long DiskRead;
    public long DiskWrite;
    public long Events;
}
