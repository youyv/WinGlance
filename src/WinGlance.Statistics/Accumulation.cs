using System.Collections.ObjectModel;
using WinGlance.Core;

namespace WinGlance.Statistics;

internal static class StatisticMetrics
{
    internal static readonly StatisticMetric[] All = Enum.GetValues<StatisticMetric>();
}

internal sealed class MetricTotal
{
    public double Integral { get; set; }
    public double ValidSeconds { get; set; }
    public double? Peak { get; set; }
    public DateTimeOffset? PeakUtc { get; set; }
    public long? Bytes { get; set; }
    public long? CpuTicks { get; set; }

    public void Add(MetricAccounting value, double fraction, DateTimeOffset end, long? bytes, long? ticks,
        bool includeCoverage = true, bool includePeak = true)
    {
        if (value.State != MetricState.Ready || value.Integral is not { } integral ||
            !double.IsFinite(integral) || integral < 0 || !double.IsFinite(value.ValidSeconds) || value.ValidSeconds <= 0) return;
        Integral += integral * fraction;
        if (includeCoverage) ValidSeconds += value.ValidSeconds * fraction;
        if (includePeak && value.Peak is { } peak && double.IsFinite(peak) && peak >= 0 && (Peak is null || peak > Peak))
        { Peak = peak; PeakUtc = end; }
        if (bytes.HasValue) Bytes = checked((Bytes ?? 0) + bytes.Value);
        if (ticks.HasValue) CpuTicks = checked((CpuTicks ?? 0) + ticks.Value);
    }

    public void AddRaw(long? bytes, long? ticks)
    {
        if (bytes.HasValue) Bytes = checked((Bytes ?? 0) + bytes.Value);
        if (ticks.HasValue) CpuTicks = checked((CpuTicks ?? 0) + ticks.Value);
    }

    public void Merge(MetricTotal other)
    {
        Integral += other.Integral;
        ValidSeconds += other.ValidSeconds;
        if (other.Peak is { } peak && (Peak is null || peak > Peak)) { Peak = peak; PeakUtc = other.PeakUtc; }
        if (other.Bytes.HasValue) Bytes = checked((Bytes ?? 0) + other.Bytes.Value);
        if (other.CpuTicks.HasValue) CpuTicks = checked((CpuTicks ?? 0) + other.CpuTicks.Value);
    }

    public MetricStatistics Snapshot(double observed) => new(ValidSeconds > 0 ? Integral : null,
        ValidSeconds, ValidSeconds > 0 ? Integral / ValidSeconds : null, Peak, Bytes, CpuTicks,
        observed > 0 ? Math.Clamp(ValidSeconds / observed, 0, 1) : 0, PeakUtc);
}

internal sealed class BucketData
{
    public double ObservedSeconds { get; set; }
    public Dictionary<StatisticMetric, MetricTotal> Metrics { get; set; } = [];
    public long UnattributedDownload { get; set; }
    public long UnattributedUpload { get; set; }
    public long UnattributedRead { get; set; }
    public long UnattributedWrite { get; set; }
    public long UnattributedEvents { get; set; }
    public long LostEvents { get; set; }
    public double EtwSeconds { get; set; }
    public MetricState EtwState { get; set; } = MetricState.WarmingUp;
    public long? MemoryCapacityMin { get; set; }
    public long? MemoryCapacityMax { get; set; }
    public long? GpuCapacityMin { get; set; }
    public long? GpuCapacityMax { get; set; }
    public bool MemoryCapacityUnknown { get; set; }
    public bool GpuCapacityUnknown { get; set; }
    public HashSet<string> NetworkScopes { get; set; } = [];

    public MetricTotal Metric(StatisticMetric metric)
    {
        if (!Metrics.TryGetValue(metric, out var value)) Metrics[metric] = value = new();
        return value;
    }

    public void Merge(BucketData other)
    {
        ObservedSeconds += other.ObservedSeconds;
        foreach (var (metric, value) in other.Metrics) Metric(metric).Merge(value);
        UnattributedDownload += other.UnattributedDownload; UnattributedUpload += other.UnattributedUpload;
        UnattributedRead += other.UnattributedRead; UnattributedWrite += other.UnattributedWrite;
        UnattributedEvents += other.UnattributedEvents; LostEvents += other.LostEvents;
        EtwSeconds += other.EtwSeconds; EtwState = other.EtwState;
        MemoryCapacityMin = Min(MemoryCapacityMin, other.MemoryCapacityMin);
        MemoryCapacityMax = Max(MemoryCapacityMax, other.MemoryCapacityMax);
        GpuCapacityMin = Min(GpuCapacityMin, other.GpuCapacityMin);
        GpuCapacityMax = Max(GpuCapacityMax, other.GpuCapacityMax);
        NetworkScopes.UnionWith(other.NetworkScopes);
        MemoryCapacityUnknown |= other.MemoryCapacityUnknown; GpuCapacityUnknown |= other.GpuCapacityUnknown;
    }

    public void Capacities(long? memory, long? gpu, string? network)
    {
        MemoryCapacityMin = Min(MemoryCapacityMin, memory); MemoryCapacityMax = Max(MemoryCapacityMax, memory);
        GpuCapacityMin = Min(GpuCapacityMin, gpu); GpuCapacityMax = Max(GpuCapacityMax, gpu);
        MemoryCapacityUnknown |= memory is null; GpuCapacityUnknown |= gpu is null;
        if (!string.IsNullOrWhiteSpace(network)) NetworkScopes.Add(network);
    }
    private static long? Min(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    public IReadOnlyDictionary<StatisticMetric, MetricStatistics> SnapshotMetrics() =>
        new ReadOnlyDictionary<StatisticMetric, MetricStatistics>(StatisticMetrics.All
            .ToDictionary(metric => metric, metric => Metrics.GetValueOrDefault(metric)?.Snapshot(ObservedSeconds)
                ?? new MetricStatistics(null, 0, null, null, null, null, 0)));

    public EtwQualityAccounting Quality() => new(UnattributedDownload, UnattributedUpload,
        UnattributedRead, UnattributedWrite, UnattributedEvents, LostEvents, EtwSeconds, EtwState);
}

internal readonly record struct BucketKey(string Session, string Day, int Kind, long Start, string Entity);
internal sealed record StoredBucket(BucketKey Key, long End, BucketData Data);
internal sealed record ApplicationEntry(string AppId, SoftwareIdentity Software, DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

internal static class ProcessAccountingAggregation
{
    internal readonly record struct Window(DateTimeOffset Start, DateTimeOffset End);

    public static IReadOnlyList<Window> ObservedWindows(IReadOnlyList<ProcessAccounting> processes, AccountingFrame frame)
    {
        var windows = processes.Select(p => ObservedWindow(p, frame)).Where(w => w.HasValue)
            .Select(w => w!.Value).OrderBy(w => w.Start).ToArray();
        var result = new List<Window>();
        foreach (var window in windows)
        {
            if (result.Count == 0 || window.Start > result[^1].End) result.Add(window);
            else if (window.End > result[^1].End) result[^1] = result[^1] with { End = window.End };
        }
        return result;
    }

    private static Window? ObservedWindow(ProcessAccounting process, AccountingFrame frame)
    {
        if (!double.IsFinite(process.ObservedSeconds) || process.ObservedSeconds <= 0) return null;
        var end = process.ObservedEndUtc ?? frame.EndUtc;
        if (end > frame.EndUtc) end = frame.EndUtc;
        var start = process.ObservedStartUtc ?? end.AddSeconds(-Math.Min(process.ObservedSeconds, frame.IntervalSeconds));
        if (start < frame.StartUtc) start = frame.StartUtc;
        return end > start ? new(start, end) : null;
    }

    private static Window LifetimeWindow(ProcessAccounting process, AccountingFrame frame)
    {
        DateTimeOffset start = frame.StartUtc;
        if (process.Identity.StartTimeUtcTicks > 0 && process.Identity.StartTimeUtcTicks <= frame.EndUtc.UtcTicks)
            start = new(process.Identity.StartTimeUtcTicks, TimeSpan.Zero);
        // An observation beginning inside the frame confirms this member was not alive earlier in the frame.
        if (process.ObservedStartUtc is { } observedStart && observedStart > frame.StartUtc) start = observedStart;
        var end = process.ObservedEndUtc ?? frame.EndUtc;
        return new(start, end);
    }

    public static bool TryMetricWindow(MetricAccounting value, AccountingFrame frame, out Window window)
    {
        window = default;
        if (value.State != MetricState.Ready || value.Integral is not { } integral || !double.IsFinite(integral) || integral < 0 ||
            !double.IsFinite(value.ValidSeconds) || value.ValidSeconds <= 0) return false;
        var end = value.EndUtc ?? frame.EndUtc;
        double interval = value.IntervalSeconds ?? value.ValidSeconds;
        if (!double.IsFinite(interval) || interval <= 0 || interval > TimeSpan.MaxValue.TotalSeconds / 2) return false;
        DateTimeOffset start;
        try { start = value.StartUtc ?? end.AddSeconds(-interval); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (end <= start) return false;
        window = new(start, end);
        return true;
    }

    // Integrals used for averages only include periods for which every observed member is measured.
    // Raw integer counters are stored separately, so a missing child never erases known transferred bytes or CPU time.
    public static IReadOnlyList<MetricAccounting> AggregateMetric(IReadOnlyList<ProcessAccounting> members,
        StatisticMetric metric, AccountingFrame frame)
    {
        if (members.Count == 1)
        {
            if (!members[0].Metrics.TryGetValue(metric, out var single) || !TryMetricWindow(single, frame, out _)) return [];
            return [single with { BytesDelta = null, CpuTicksDelta = null }];
        }
        if (TryCommonWindow(members, metric, frame, out var common)) return common is null ? [] : [common];
        var observations = members.Select(p => ObservedWindow(p, frame)).ToArray();
        var lifetimes = members.Select(p => LifetimeWindow(p, frame)).ToArray();
        var values = members.Select(p => p.Metrics.GetValueOrDefault(metric)).ToArray();
        var windows = values.Select(v => v is not null && TryMetricWindow(v, frame, out var window) ? (Window?)window : null).ToArray();
        var boundaries = windows.Concat(observations).Where(w => w.HasValue)
            .SelectMany(w => new[] { w!.Value.Start, w.Value.End }).Distinct().Order().ToArray();
        var result = new List<MetricAccounting>();
        for (int i = 1; i < boundaries.Length; i++)
        {
            var start = boundaries[i - 1]; var end = boundaries[i];
            var active = Enumerable.Range(0, members.Count)
                .Where(index => windows[index] is { } w && w.Start <= start && w.End >= end).ToArray();
            if (active.Length == 0 || Enumerable.Range(0, members.Count).Any(index =>
                lifetimes[index].Start < end && lifetimes[index].End > start && !active.Contains(index))) continue;
            double seconds = (end - start).TotalSeconds;
            double fraction = active.Min(index => Math.Min(1, values[index]!.ValidSeconds / (windows[index]!.Value.End - windows[index]!.Value.Start).TotalSeconds));
            if (fraction <= 0 || !double.IsFinite(fraction)) continue;
            double rate;
            if (metric == StatisticMetric.Gpu && active.Length > 1)
            {
                if (active.Any(index => members[index].GpuEngines is null)) continue;
                var engines = active.SelectMany(index => members[index].GpuEngines!).ToArray();
                if (engines.Any(entry => !TryMetricWindow(entry.Value, frame, out var w) || w.Start > start || w.End < end)) continue;
                rate = engines.Length == 0 ? 0 : engines.GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Max(group => group.Sum(entry => entry.Value.Integral!.Value / entry.Value.ValidSeconds));
                if (rate > 100.000001) continue;
                rate = Math.Min(100, rate);
            }
            else rate = active.Sum(index => values[index]!.Integral!.Value / values[index]!.ValidSeconds);
            if (!double.IsFinite(rate) || rate < 0) continue;
            double peak = rate;
            var peakUtc = end;
            if (members.Count == 1 && values[0]!.Peak is { } sourcePeak && double.IsFinite(sourcePeak) && sourcePeak >= 0)
            {
                var sourcePeakUtc = values[0]!.PeakUtc ?? windows[0]!.Value.End;
                if (sourcePeakUtc > start && sourcePeakUtc <= end && sourcePeak > peak) { peak = sourcePeak; peakUtc = sourcePeakUtc; }
            }
            result.Add(new(rate * seconds * fraction, seconds * fraction, peak,
                IntervalSeconds: seconds, StartUtc: start, EndUtc: end, PeakUtc: peakUtc));
        }
        return result;
    }

    // The usual steady-state ETW/PDH window and synthetic gauge samples have exactly matching boundaries.
    // The general interval path remains responsible for partial, asynchronous and lifecycle-crossing windows.
    private static bool TryCommonWindow(IReadOnlyList<ProcessAccounting> members, StatisticMetric metric,
        AccountingFrame frame, out MetricAccounting? result)
    {
        result = null;
        if (members.Count == 0) return true;
        Window common = default;
        double seconds = 0, integral = 0;
        for (int index = 0; index < members.Count; index++)
        {
            if (!members[index].Metrics.TryGetValue(metric, out var value) || !TryMetricWindow(value, frame, out var window)) return false;
            if (index == 0) { common = window; seconds = value.ValidSeconds; }
            else if (common != window || seconds != value.ValidSeconds) return false;
            integral += value.Integral!.Value;
        }
        if (metric == StatisticMetric.Gpu)
        {
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                if (member.GpuEngines is null) return true;
                foreach (var (engine, value) in member.GpuEngines)
                {
                    if (!TryMetricWindow(value, frame, out var window) || window != common || value.ValidSeconds != seconds) return false;
                    engines[engine] = engines.GetValueOrDefault(engine) + value.Integral!.Value / seconds;
                }
            }
            double busiest = engines.Count == 0 ? 0 : engines.Values.Max();
            if (busiest > 100.000001) return true;
            integral = Math.Min(100, busiest) * seconds;
        }
        if (!double.IsFinite(integral)) return true;
        result = new(integral, seconds, integral / seconds, IntervalSeconds: (common.End - common.Start).TotalSeconds,
            StartUtc: common.Start, EndUtc: common.End, PeakUtc: common.End);
        return true;
    }
}
