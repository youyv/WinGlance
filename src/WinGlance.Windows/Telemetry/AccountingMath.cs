using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

internal static class AccountingMath
{
    // A delayed frame is not an observation of the intervening gauges.
    internal const double MaximumContinuousSeconds = 5;
    internal static bool Continuous(double seconds) => double.IsFinite(seconds) && seconds > 0.001 && seconds <= MaximumContinuousSeconds;

    internal static MetricAccounting Missing(MetricState state, double seconds, string? detail = null) =>
        new(null, 0, null, state, IntervalSeconds: Math.Max(0, seconds), Detail: detail);

    internal static MetricAccounting Gauge(double? value, double seconds, bool continuous,
        MetricState missingState = MetricState.Error, string? detail = null) =>
        continuous && Continuous(seconds) && value is { } actual && double.IsFinite(actual) && actual >= 0
            ? new(actual * seconds, seconds, actual, Detail: detail, IntervalSeconds: seconds)
            : Missing(value.HasValue && !continuous ? MetricState.WarmingUp : missingState, seconds, detail);

    internal static MetricAccounting Bytes(long? bytes, double seconds, bool continuous,
        MetricState missingState = MetricState.Error, string? detail = null) =>
        continuous && Continuous(seconds) && bytes is >= 0
            ? new(bytes.Value, seconds, bytes.Value / seconds, BytesDelta: bytes.Value, IntervalSeconds: seconds, Detail: detail)
            : Missing(!continuous && missingState == MetricState.Ready ? MetricState.WarmingUp : missingState, seconds, detail);
}
