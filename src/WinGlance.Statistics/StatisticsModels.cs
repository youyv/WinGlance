using WinGlance.Core;

namespace WinGlance.Statistics;

public enum StatisticsRange { CurrentSession, Today, LastSevenDays }

public sealed record MetricStatistics(double? Integral, double ValidSeconds, double? Average,
    double? Peak, long? Bytes, long? CpuTicks, double Coverage, DateTimeOffset? PeakUtc = null);

public sealed record StatisticsSummary(double ObservedSeconds,
    IReadOnlyDictionary<StatisticMetric, MetricStatistics> Metrics);

public sealed record ApplicationStatistics(string AppId, SoftwareIdentity Software, double ObservedSeconds,
    bool IsRunning, DateTimeOffset LastSeenUtc, IReadOnlyDictionary<StatisticMetric, MetricStatistics> Metrics);

public sealed record StatisticsTrendPoint(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    double ObservedSeconds, IReadOnlyDictionary<StatisticMetric, MetricStatistics> Metrics);

public sealed record ApplicationStatisticsDetail(ApplicationStatistics? Application,
    IReadOnlyList<StatisticsTrendPoint> Trend);

public sealed record StatisticsSnapshot(StatisticsRange Range, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    Guid SessionId, StatisticsSummary System, IReadOnlyList<ApplicationStatistics> Applications,
    IReadOnlyList<StatisticsTrendPoint> SystemTrend, DateTimeOffset? LastRecordedUtc,
    DateTimeOffset? LastResetUtc, string? Warning, DateTimeOffset? LastSavedUtc = null,
    long? TotalMemoryBytes = null, long? DedicatedGpuCapacityBytes = null,
    string? NetworkScope = null, EtwQualityAccounting? EtwQuality = null);
