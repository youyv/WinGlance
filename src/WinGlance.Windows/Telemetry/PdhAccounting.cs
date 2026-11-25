using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

internal sealed partial class PdhMetrics
{
    private readonly Dictionary<string, Dictionary<string, RawCounter>> _rawPrevious = [];
    private readonly Dictionary<string, (uint Type, long TimeBase)> _rawTypes = [];
    private readonly DxgiAdapters _adapters = new();
    private long _accountingTimestamp;
    private DateTimeOffset _accountingUtc;

    private void AddAccountingCounters()
    {
        Add("diskReadAccounting", @"\PhysicalDisk(*)\Disk Read Bytes/sec");
        Add("diskWriteAccounting", @"\PhysicalDisk(*)\Disk Write Bytes/sec");
        Add("adapterDedicated", @"\GPU Adapter Memory(*)\Dedicated Usage");
        Add("adapterShared", @"\GPU Adapter Memory(*)\Shared Usage");
    }

    private PdhAccountingSample ReadAccounting(uint status, long timestamp, DateTimeOffset endUtc, bool reset,
        double? gpuPercent, IReadOnlyDictionary<string, double> engines, MetricState gpuState)
    {
        double seconds = _accountingTimestamp == 0 ? 0 : (timestamp - _accountingTimestamp) / (double)Stopwatch.Frequency;
        var startUtc = _accountingTimestamp == 0 ? endUtc : _accountingUtc;
        _accountingTimestamp = timestamp;
        _accountingUtc = endUtc;
        bool continuous = !reset && AccountingMath.Continuous(seconds) && status == 0;
        var diskRead = ReadDiskDelta("diskReadAccounting", seconds, continuous, status == 0, out var diskReads);
        var diskWrite = ReadDiskDelta("diskWriteAccounting", seconds, continuous, status == 0, out var diskWrites);
        var disks = diskReads.Keys.Concat(diskWrites.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
        {
            var read = diskReads.GetValueOrDefault(id) ?? AccountingMath.Missing(MetricState.Error, seconds, "该磁盘读取计数器不可用");
            var write = diskWrites.GetValueOrDefault(id) ?? AccountingMath.Missing(MetricState.Error, seconds, "该磁盘写入计数器不可用");
            var state = read.State == MetricState.Ready && write.State == MetricState.Ready ? MetricState.Ready
                : read.State == MetricState.Error || write.State == MetricState.Error ? MetricState.Error
                : read.State == MetricState.Unsupported || write.State == MetricState.Unsupported ? MetricState.Unsupported
                : MetricState.WarmingUp;
            return new PhysicalDiskSample(id, $"磁盘 {id}", read.State == MetricState.Ready ? read.Peak : null,
                write.State == MetricState.Ready ? write.Peak : null, state,
                state == MetricState.Ready ? "物理磁盘字节差分；实例标识可能随分区变化" : read.Detail ?? write.Detail);
        }).ToArray();
        var dedicated = status == 0 ? Read("adapterDedicated") : CounterArray.Failed;
        var shared = status == 0 ? Read("adapterShared") : CounterArray.Failed;
        var dedicatedByAdapter = AdapterMemory(dedicated);
        var sharedByAdapter = AdapterMemory(shared);
        var capacities = _adapters.Read();
        var ids = dedicatedByAdapter.Keys.Concat(sharedByAdapter.Keys).Concat(capacities.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var adapters = new List<GpuAdapterAccounting>();
        foreach (string id in ids)
        {
            capacities.TryGetValue(id, out var capacity);
            long? dedicatedBytes = dedicatedByAdapter.TryGetValue(id, out var d) ? d : null;
            long? sharedBytes = sharedByAdapter.TryGetValue(id, out var s) ? s : null;
            double? usage = gpuState == MetricState.Ready && gpuPercent.HasValue
                ? engines.Where(x => AdapterId(x.Key) == id).Select(x => x.Value).DefaultIfEmpty(0).Max() : null;
            adapters.Add(new(id, capacity?.Name ?? id, usage, dedicatedBytes, sharedBytes,
                capacity?.DedicatedBytes, capacity?.SharedBytes,
                dedicatedBytes.HasValue && sharedBytes.HasValue ? MetricState.Ready : MetricState.Unsupported,
                "适配器级 WDDM 显存；容量来自 DXGI 描述，专用与共享分别记录"));
        }
        long? totalDedicated = SumAvailable(adapters.Select(x => x.DedicatedBytes));
        long? totalShared = SumAvailable(adapters.Select(x => x.SharedBytes));
        long? totalCapacity = SumAvailable(adapters.Select(x => x.DedicatedCapacityBytes));
        MetricAccounting Stamp(MetricAccounting metric) => metric with
        { StartUtc = endUtc.AddSeconds(-(metric.IntervalSeconds ?? seconds)), EndUtc = endUtc };
        return new(seconds, startUtc, endUtc, continuous, Stamp(diskRead), Stamp(diskWrite),
            Stamp(AccountingMath.Gauge(gpuPercent, seconds, continuous, gpuState)),
            Stamp(AccountingMath.Gauge(totalDedicated, seconds, continuous, MetricState.Unsupported)),
            Stamp(AccountingMath.Gauge(totalShared, seconds, continuous, MetricState.Unsupported)), adapters, totalCapacity)
        { PhysicalDisks = disks };
    }

    private static Dictionary<string, long> AdapterMemory(CounterArray array)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!array.Successful) return result;
        var invalid = array.InvalidNames.Select(AdapterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.Values)
        {
            string? id = AdapterId(item.Key);
            if (id is null || invalid.Contains(id) || item.Value < 0 || item.Value >= long.MaxValue) continue;
            try { result[id] = checked(result.GetValueOrDefault(id) + (long)item.Value); }
            catch (OverflowException) { invalid.Add(id); result.Remove(id); }
        }
        return result;
    }

    private static string? AdapterId(string value)
    {
        var match = AdapterPattern().Match(value);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }
    [GeneratedRegex(@"luid_0x[0-9a-fA-F]{8}_0x[0-9a-fA-F]{8}", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterPattern();

    private static long? SumAvailable(IEnumerable<long?> values)
    {
        bool any = false;
        long sum = 0;
        foreach (var value in values)
        {
            any = true;
            if (!value.HasValue) return null;
            try { sum = checked(sum + value.Value); } catch (OverflowException) { return null; }
        }
        return any ? sum : null;
    }

    private MetricAccounting ReadDiskDelta(string key, double fallbackSeconds, bool continuous, bool collected,
        out Dictionary<string, MetricAccounting> devices)
    {
        devices = new(StringComparer.OrdinalIgnoreCase);
        MetricAccounting Missing(MetricState state, string detail) => AccountingMath.Missing(state, fallbackSeconds, detail);
        if (!_counters.TryGetValue(key, out var counter)) return Missing(MetricState.Unsupported, "物理磁盘原始计数器不可用");
        if (!collected) { _rawPrevious.Remove(key); return Missing(MetricState.Error, "PDH 采集失败"); }
        if (!_rawTypes.TryGetValue(key, out var metadata))
        {
            uint size = 0;
            _ = PdhGetCounterInfo(counter, false, ref size, 0);
            if (size < 8 || size > 1024 * 1024) return Missing(MetricState.Error, "无法核验磁盘计数器类型");
            nint info = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetCounterInfo(counter, false, ref size, info) != 0 || PdhGetCounterTimeBase(counter, out long frequency) != 0 || frequency <= 0)
                    return Missing(MetricState.Error, "无法读取磁盘计数器时间基准");
                metadata = ((uint)Marshal.ReadInt32(info, 4), frequency);
                _rawTypes[key] = metadata;
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        // PERF_COUNTER_BULK_COUNT: numerator is the cumulative 64-bit byte count, second value is the time base.
        if (metadata.Type != 0x10410500) return Missing(MetricState.Unsupported, $"未支持的磁盘原始计数类型 0x{metadata.Type:X8}");
        var current = ReadRawArray(counter);
        if (current is null || current.Count == 0)
        { _rawPrevious.Remove(key); return Missing(MetricState.Error, "磁盘原始计数数组不可用"); }
        _rawPrevious.TryGetValue(key, out var previous);
        _rawPrevious[key] = current;
        foreach (string id in current.Keys)
            devices[id] = Missing(MetricState.WarmingUp, "磁盘首次出现、设备范围变化或计数间断，正在建立基线");
        if (!continuous || previous is null || previous.Count != current.Count || !previous.Keys.All(current.ContainsKey))
            return Missing(MetricState.WarmingUp, "磁盘设备范围变化或采集间断，正在建立基线");
        long bytes = 0;
        double? duration = null;
        foreach (var item in current)
        {
            var old = previous[item.Key];
            var value = item.Value;
            if (value.First < old.First || value.Second <= old.Second)
                return Missing(MetricState.WarmingUp, "磁盘计数器已重置");
            double elapsed = (value.Second - old.Second) / (double)metadata.TimeBase;
            if (!AccountingMath.Continuous(elapsed) || duration.HasValue && Math.Abs(duration.Value - elapsed) > 0.01)
                return Missing(MetricState.WarmingUp, "磁盘计数区间不连续或设备时间基准不一致");
            duration = elapsed;
            devices[item.Key] = AccountingMath.Bytes(value.First - old.First, elapsed, true);
            try { bytes = checked(bytes + value.First - old.First); }
            catch (OverflowException) { return Missing(MetricState.Error, "磁盘累计计数溢出"); }
        }
        return AccountingMath.Bytes(bytes, duration ?? fallbackSeconds, true,
            detail: "物理磁盘设备原始字节差分，不含 _Total 重复项");
    }

    private static Dictionary<string, RawCounter>? ReadRawArray(nint counter)
    {
        uint size = 0, count = 0;
        uint sizing = PdhGetRawCounterArray(counter, ref size, ref count, 0);
        if (sizing != MoreData || size == 0 || size > 16 * 1024 * 1024) return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint capacity = size;
                uint status = PdhGetRawCounterArray(counter, ref size, ref count, buffer);
                if (status == MoreData && size <= 16 * 1024 * 1024) continue;
                if (status != 0 || (ulong)count * (uint)Marshal.SizeOf<RawItem>() > capacity) return null;
                var result = new Dictionary<string, RawCounter>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<RawItem>(buffer + i * Marshal.SizeOf<RawItem>());
                    string? name = Marshal.PtrToStringUni(item.Name);
                    if (name == "_Total") continue;
                    if (name is null || item.Counter.Status > 1 || item.Counter.First < 0 || !result.TryAdd(name, item.Counter)) return null;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct RawCounter
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(4)] public uint TimeLow;
        [FieldOffset(8)] public uint TimeHigh;
        [FieldOffset(16)] public long First;
        [FieldOffset(24)] public long Second;
        [FieldOffset(32)] public uint MultiCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawItem { public nint Name; public RawCounter Counter; }
    [DllImport("pdh.dll", EntryPoint = "PdhGetRawCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetRawCounterArray(nint counter, ref uint size, ref uint count, nint buffer);
    [DllImport("pdh.dll", EntryPoint = "PdhGetCounterInfoW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetCounterInfo(nint counter, [MarshalAs(UnmanagedType.Bool)] bool explain, ref uint size, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhGetCounterTimeBase(nint counter, out long timeBase);
}

internal sealed record PdhAccountingSample(double IntervalSeconds, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    bool Continuous, MetricAccounting DiskRead, MetricAccounting DiskWrite, MetricAccounting Gpu,
    MetricAccounting DedicatedGpuMemory, MetricAccounting SharedGpuMemory,
    IReadOnlyList<GpuAdapterAccounting> Adapters, long? DedicatedCapacityBytes)
{
    public IReadOnlyList<PhysicalDiskSample> PhysicalDisks { get; init; } = [];
}
