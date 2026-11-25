namespace WinGlance.Core;

/// <summary>Combines process observations without converting missing observations to zero.</summary>
public static class SoftwareAggregator
{
    public static IReadOnlyList<SoftwareSample> Aggregate(IReadOnlyList<ProcessSample> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        return processes.GroupBy(p => p.Software.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                // A process may appear twice when a snapshot straddles a process refresh.
                var members = group.GroupBy(p => p.Identity).Select(g => g.Last()).ToArray();
                var identity = members.Select(p => p.Software)
                    .OrderByDescending(s => s.IconPath is not null)
                    .ThenByDescending(s => s.DisplayName != s.ProcessName).First();
                return new SoftwareSample(group.Key, identity, members,
                    Sum(members, p => p.CpuPercent), Sum(members, p => p.PrivateWorkingSetBytes),
                    Sum(members, p => p.DownloadBytesPerSecond), Sum(members, p => p.UploadBytesPerSecond),
                    Sum(members, p => p.DiskReadBytesPerSecond), Sum(members, p => p.DiskWriteBytesPerSecond),
                    AggregateGpu(members), Sum(members, p => p.DedicatedGpuBytes), Sum(members, p => p.SharedGpuBytes));
            }).ToArray();
    }

    private static double? Sum(IEnumerable<ProcessSample> values, Func<ProcessSample, double?> select)
    {
        double sum = 0;
        foreach (var process in values)
        {
            var value = select(process);
            if (value is null || !double.IsFinite(value.Value) || value.Value < 0) return null;
            sum += value.Value;
            if (!double.IsFinite(sum)) return null;
        }
        return sum;
    }

    private static long? Sum(IEnumerable<ProcessSample> values, Func<ProcessSample, long?> select)
    {
        long sum = 0;
        foreach (var process in values)
        {
            var value = select(process);
            if (value is null || value.Value < 0) return null;
            try { sum = checked(sum + value.Value); }
            catch (OverflowException) { return null; }
        }
        return sum;
    }

    private static double? AggregateGpu(IEnumerable<ProcessSample> members)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in members)
        {
            if (process.GpuState != MetricState.Ready) return null;
            foreach (var (engine, value) in process.GpuEngines)
            {
                if (!double.IsFinite(value) || value < -0.000001) return null;
                // Keys identify an adapter + physical engine, never a process-specific PDH instance.
                engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, value);
            }
        }
        var maximum = engines.Count == 0 ? 0 : engines.Values.Max();
        // Allow floating-point rounding, but surface inconsistent counters instead of hiding over-counting.
        return maximum > 100.000001 ? null : Math.Min(maximum, 100);
    }
}
