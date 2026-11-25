using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

/// <summary>One PDH query, English counter paths, and bounded transient buffers.</summary>
internal sealed partial class PdhMetrics : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint DoubleFormat = 0x00000200 | 0x00008000; // DOUBLE | NOCAP100
    private nint _query;
    private readonly Dictionary<string, nint> _counters = [];
    private bool _primed;

    public PdhMetrics()
    {
        if (PdhOpenQuery(null, 0, out _query) != 0) return;
        Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
        Add("dedicated", @"\GPU Process Memory(*)\Dedicated Usage");
        Add("shared", @"\GPU Process Memory(*)\Shared Usage");
        Add("pidV2", @"\Process V2(*)\Process ID");
        Add("workingV2", @"\Process V2(*)\Working Set - Private");
        Add("pid", @"\Process(*)\ID Process");
        Add("working", @"\Process(*)\Working Set - Private");
        Add("diskRead", @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        Add("diskWrite", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        AddAccountingCounters();
    }

    private void Add(string key, string path)
    {
        if (PdhAddEnglishCounter(_query, path, 0, out var counter) == 0)
            _counters.Add(key, counter);
    }

    public PdhSnapshot Sample(bool resetAccounting = false)
    {
        bool wasPrimed = _primed;
        uint status = _query == 0 ? uint.MaxValue : PdhCollectQueryData(_query);
        long collectedTimestamp = Stopwatch.GetTimestamp();
        var collectedUtc = DateTimeOffset.UtcNow;
        _primed = status == 0;
        var gpu = new Dictionary<int, Dictionary<string, double>>();
        var dedicated = new Dictionary<int, long>();
        var shared = new Dictionary<int, long>();
        var working = new Dictionary<int, long>();
        CounterArray gpuValues = CounterArray.Failed;
        CounterArray dedicatedValues = CounterArray.Failed;
        CounterArray sharedValues = CounterArray.Failed;
        bool usingProcessV2 = false;
        if (status == 0)
        {
            var ids = Read("pidV2", omitAmbiguous: true).Values;
            usingProcessV2 = ids.Count > 0;
            if (!usingProcessV2) ids = Read("pid", omitAmbiguous: true).Values;
            foreach (var item in Read(usingProcessV2 ? "workingV2" : "working", omitAmbiguous: true).Values)
                if (ids.TryGetValue(item.Key, out var id) && id > 0 && id <= int.MaxValue)
                    working[(int)id] = (long)Math.Max(0, item.Value);
            if (wasPrimed)
            {
                gpuValues = Read("gpu");
                foreach (var item in gpuValues.Values)
                {
                    var match = PidPattern().Match(item.Key);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out int pid)) continue;
                    if (!gpu.TryGetValue(pid, out var engines)) gpu[pid] = engines = [];
                    string engine = InstanceSuffix().Replace(item.Key[match.Length..], "");
                    engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, item.Value);
                }
            }
            dedicatedValues = Read("dedicated");
            sharedValues = Read("shared");
            AddMemory(dedicatedValues.Values, dedicated);
            AddMemory(sharedValues.Values, shared);
        }
        var totalEngines = new Dictionary<string, double>();
        foreach (var process in gpu.Values)
            foreach (var engine in process)
                totalEngines[engine.Key] = totalEngines.GetValueOrDefault(engine.Key) + engine.Value;
        var capability = !_counters.ContainsKey("gpu")
            ? new Capability(MetricState.Unsupported, "驱动未提供 GPU Engine 计数器")
            : status != 0 ? new Capability(MetricState.Error, $"GPU 性能计数器不可用（0x{status:X8}）")
            : !wasPrimed ? new Capability(MetricState.WarmingUp, "GPU 正在建立采样基线")
            : !gpuValues.Successful ? new Capability(MetricState.Error, "GPU 引擎数组读取失败，本窗口数据不可用")
            : new Capability(MetricState.Ready, "WDDM GPU 引擎计数器；显存为进程记账值，共享映射可能重复") ;
        var invalidGpu = InvalidPids(gpuValues);
        var invalidDedicated = InvalidPids(dedicatedValues);
        var invalidShared = InvalidPids(sharedValues);
        foreach (var pid in invalidGpu.Pids) gpu.Remove(pid);
        double? gpuPercent = !gpuValues.Successful || gpuValues.InvalidNames.Count > 0
            ? null : totalEngines.Values.DefaultIfEmpty(0).Max();
        if (invalidGpu.Unrecognized)
            capability = new Capability(MetricState.Error, "GPU 引擎存在无法识别的无效实例，本窗口数据不可用");
        if (gpuPercent > 100)
        {
            // A broken counter/driver must not be disguised as a legitimate 100% sample.
            capability = new Capability(MetricState.Error, "GPU 引擎计数器超过有效范围，本窗口数据不可用");
            gpuPercent = null;
            gpu.Clear();
        }
        var memoryCapability = !_counters.ContainsKey("dedicated") || !_counters.ContainsKey("shared")
            ? new Capability(MetricState.Unsupported, "驱动未提供完整 GPU 进程显存计数器")
            : !dedicatedValues.Successful || !sharedValues.Successful
                ? new Capability(MetricState.Error, "GPU 显存数组读取失败，缺失项显示 —")
                : dedicatedValues.InvalidNames.Count > 0 || sharedValues.InvalidNames.Count > 0
                    ? new Capability(MetricState.Error, "部分 GPU 显存实例无效，对应进程显示 —")
                    : new Capability(MetricState.Ready, "显存数组读取成功；没有实例的进程为 0，共享映射合计可能重复");
        return new PdhSnapshot(gpu, dedicated, shared, working,
            gpuPercent,
            wasPrimed && status == 0 ? ReadScalar("diskRead") : null,
            wasPrimed && status == 0 ? ReadScalar("diskWrite") : null, capability,
            _counters.ContainsKey("workingV2") || _counters.ContainsKey("working"), usingProcessV2,
            dedicatedValues.Successful && !invalidDedicated.Unrecognized, sharedValues.Successful && !invalidShared.Unrecognized,
            invalidDedicated.Pids, invalidShared.Pids, invalidGpu.Pids, memoryCapability)
        {
            Accounting = ReadAccounting(status, collectedTimestamp, collectedUtc, resetAccounting,
                gpuPercent, totalEngines, capability.State)
        };
    }

    private static (HashSet<int> Pids, bool Unrecognized) InvalidPids(CounterArray values)
    {
        var pids = new HashSet<int>();
        bool unrecognized = false;
        foreach (string name in values.InvalidNames)
        {
            var match = PidPattern().Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out int pid)) pids.Add(pid);
            else unrecognized = true;
        }
        return (pids, unrecognized);
    }

    private static void AddMemory(Dictionary<string, double> values, Dictionary<int, long> target)
    {
        foreach (var item in values)
        {
            var match = PidPattern().Match(item.Key);
            if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out int pid))
                target[pid] = target.GetValueOrDefault(pid) + (long)Math.Max(0, item.Value);
        }
    }

    private double? ReadScalar(string key)
    {
        if (!_counters.TryGetValue(key, out var counter)) return null;
        return PdhGetFormattedCounterValue(counter, DoubleFormat, out _, out var value) == 0 && value.Status <= 1 && double.IsFinite(value.Value)
            ? Math.Max(0, value.Value) : null;
    }

    private CounterArray Read(string key, bool omitAmbiguous = false)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var originalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool successful = false;
        if (!_counters.TryGetValue(key, out var counter)) return CounterArray.Failed;
        uint size = 0, count = 0;
        uint sizingStatus = PdhGetFormattedCounterArray(counter, DoubleFormat, ref size, ref count, 0);
        if (sizingStatus == 0 && count == 0) return new CounterArray([], [], true);
        if (sizingStatus != MoreData || size == 0 || size > 16 * 1024 * 1024) return CounterArray.Failed;
        // Instances can change between the size query and the second call. Retry once.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint capacity = size;
                uint status = PdhGetFormattedCounterArray(counter, DoubleFormat, ref size, ref count, buffer);
                if (status == MoreData && size <= 16 * 1024 * 1024) continue;
                if (status != 0) break;
                int stride = Marshal.SizeOf<CounterItem>();
                if ((ulong)count * (ulong)stride > capacity) break;
                successful = true;
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + i * stride);
                    string? name = Marshal.PtrToStringUni(item.Name);
                    if (name is not null)
                    {
                        // V2 instance names include PID. Legacy Process array names can repeat.
                        // Never join legacy duplicate instances by array position: PDH does not
                        // promise matching ordering between independent formatted counter arrays.
                        if (result.ContainsKey(name)) ambiguous.Add(name);
                        string instance = name;
                        for (int duplicate = 1; result.ContainsKey(instance); duplicate++) instance = $"{name}#{duplicate}";
                        originalNames[instance] = name;
                        result[instance] = item.Value.Status <= 1 && double.IsFinite(item.Value.Value) && item.Value.Value >= 0
                            ? item.Value.Value : double.NaN;
                    }
                    else successful = false;
                }
                break;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        if (!successful) return CounterArray.Failed;
        var invalidNames = result.Where(x => !double.IsFinite(x.Value) || (omitAmbiguous && ambiguous.Contains(originalNames[x.Key])))
            .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new CounterArray(result.Where(x => !invalidNames.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase), invalidNames, true);
    }

    private sealed record CounterArray(Dictionary<string, double> Values, HashSet<string> InvalidNames, bool Successful)
    {
        public static CounterArray Failed => new([], [], false);
    }

    public void Dispose()
    {
        if (_query != 0) { PdhCloseQuery(_query); _query = 0; }
    }

    [GeneratedRegex(@"^pid_(\d+)_", RegexOptions.CultureInvariant)] private static partial Regex PidPattern();
    [GeneratedRegex(@"#\d+$", RegexOptions.CultureInvariant)] private static partial Regex InstanceSuffix();
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public nint Name; public CounterValue Value; }
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, nuint userData, out nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, ref uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);
}

internal sealed record PdhSnapshot(Dictionary<int, Dictionary<string, double>> Engines,
    Dictionary<int, long> Dedicated, Dictionary<int, long> Shared, Dictionary<int, long> WorkingSet,
    double? GpuPercent, double? DiskRead, double? DiskWrite, Capability GpuCapability, bool WorkingSetSupported,
    bool UsingProcessV2, bool DedicatedAvailable, bool SharedAvailable,
    HashSet<int> InvalidDedicatedPids, HashSet<int> InvalidSharedPids, HashSet<int> InvalidGpuPids,
    Capability GpuMemoryCapability)
{
    public PdhAccountingSample? Accounting { get; init; }
}
