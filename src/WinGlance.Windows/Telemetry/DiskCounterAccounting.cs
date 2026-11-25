using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

internal readonly record struct DiskCounterValue(long Bytes, long Timestamp, uint Status = 0);

internal static class DiskCounterAccounting
{
    internal static MetricAccounting Calculate(IReadOnlyDictionary<string, DiskCounterValue> current,
        IReadOnlyDictionary<string, DiskCounterValue>? previous, long timeBase, double fallbackSeconds,
        bool continuous, out Dictionary<string, MetricAccounting> devices)
    {
        devices = new(StringComparer.OrdinalIgnoreCase);
        MetricAccounting Missing(MetricState state, string detail) => AccountingMath.Missing(state, fallbackSeconds, detail);
        bool sameScope = previous is not null && previous.Count == current.Count && previous.Keys.All(current.ContainsKey);
        bool complete = continuous && sameScope && current.Count > 0;
        bool error = false;
        long bytes = 0;
        double minDuration = double.PositiveInfinity, maxDuration = 0, sumDuration = 0;
        int readyCount = 0;

        foreach (var (id, value) in current)
        {
            MetricAccounting metric;
            if (value.Status > 1 || value.Bytes < 0 || timeBase <= 0)
                metric = Missing(MetricState.Error, "该磁盘原始计数器无效");
            else if (!continuous || previous is null || !previous.TryGetValue(id, out var old) || old.Status > 1 || old.Bytes < 0)
                metric = Missing(MetricState.WarmingUp, "磁盘首次出现或计数间断，正在建立基线");
            else if (value.Bytes < old.Bytes || value.Timestamp <= old.Timestamp)
                metric = Missing(MetricState.WarmingUp, "该磁盘计数器已重置");
            else
            {
                try
                {
                    double elapsed = checked(value.Timestamp - old.Timestamp) / (double)timeBase;
                    metric = AccountingMath.Continuous(elapsed)
                        ? AccountingMath.Bytes(checked(value.Bytes - old.Bytes), elapsed, true)
                        : Missing(MetricState.WarmingUp, "该磁盘计数区间不连续");
                }
                catch (OverflowException)
                {
                    metric = Missing(MetricState.Error, "该磁盘计数差分溢出");
                }
            }
            devices[id] = metric;
            if (metric.State != MetricState.Ready)
            {
                complete = false;
                error |= metric.State == MetricState.Error;
                continue;
            }

            double duration = metric.ValidSeconds;
            minDuration = Math.Min(minDuration, duration);
            maxDuration = Math.Max(maxDuration, duration);
            sumDuration += duration;
            readyCount++;
            try { bytes = checked(bytes + metric.BytesDelta!.Value); }
            catch (OverflowException) { complete = false; error = true; }
        }

        // Individual device observations stay valid even if the aggregate has missing devices,
        // incompatible intervals, or an overflowing sum. Never stop the device loop early.
        if (error) return Missing(MetricState.Error, "部分磁盘计数器无效或累计计数溢出");
        if (!complete) return Missing(MetricState.WarmingUp, "磁盘设备范围变化或部分计数间断，合计正在建立基线");
        if (maxDuration - minDuration > 0.01)
            return Missing(MetricState.WarmingUp, "磁盘设备计数区间不一致，合计暂不可用");
        return AccountingMath.Bytes(bytes, sumDuration / readyCount, true,
            detail: "物理磁盘设备原始字节差分，不含 _Total 重复项");
    }
}
