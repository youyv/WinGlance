namespace WinGlance.Core;

public enum StatisticMetric
{
    Cpu, Gpu, Memory, DedicatedGpuMemory, SharedGpuMemory,
    Download, Upload, DiskRead, DiskWrite
}

/// <summary>Integral is percent-seconds, byte-seconds, or transferred bytes. Missing values are never zero.</summary>
public sealed record MetricAccounting(double? Integral, double ValidSeconds, double? Peak = null,
    MetricState State = MetricState.Ready, long? BytesDelta = null, long? CpuTicksDelta = null,
    double? IntervalSeconds = null, string? Detail = null, DateTimeOffset? StartUtc = null,
    DateTimeOffset? EndUtc = null, DateTimeOffset? PeakUtc = null);

public sealed record ProcessAccounting(ProcessIdentity Identity, SoftwareIdentity Software,
    double ObservedSeconds, IReadOnlyDictionary<StatisticMetric, MetricAccounting> Metrics,
    IReadOnlyDictionary<string, MetricAccounting>? GpuEngines = null,
    DateTimeOffset? ObservedStartUtc = null, DateTimeOffset? ObservedEndUtc = null);

public sealed record AccountingFrame(Guid CollectorRunId, long Sequence, DateTimeOffset StartUtc,
    DateTimeOffset EndUtc, double IntervalSeconds, IReadOnlyList<ProcessAccounting> Processes,
    SystemAccounting? System = null, bool Discontinuity = false, string? DiscontinuityReason = null);
