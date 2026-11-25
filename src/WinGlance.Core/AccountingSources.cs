namespace WinGlance.Core;

/// <summary>Integer source measurements accompanying the mergeable statistics metrics.</summary>
public sealed record SystemAccounting(IReadOnlyDictionary<StatisticMetric, MetricAccounting> Metrics,
    string? NetworkScope = null, IReadOnlyList<NetworkInterfaceAccounting>? Networks = null,
    IReadOnlyList<GpuAdapterAccounting>? GpuAdapters = null, EtwQualityAccounting? EtwQuality = null,
    long? CpuBusyTicksDelta = null, long? CpuTotalTicksDelta = null, int LogicalProcessors = 0,
    long? TotalMemoryBytes = null, long? DedicatedGpuCapacityBytes = null);

public sealed record NetworkInterfaceAccounting(string Id, string Name, ulong InterfaceLuid,
    long? DownloadBytesDelta, long? UploadBytesDelta, double IntervalSeconds,
    MetricState State, string? Detail = null);

public sealed record GpuAdapterAccounting(string Id, string Name, double? UtilizationPercent,
    long? DedicatedBytes, long? SharedBytes, long? DedicatedCapacityBytes, long? SharedCapacityBytes,
    MetricState State, string? Detail = null);

public sealed record EtwQualityAccounting(long UnattributedDownloadBytes, long UnattributedUploadBytes,
    long UnattributedDiskReadBytes, long UnattributedDiskWriteBytes, long UnattributedEvents,
    long LostEvents, double IntervalSeconds, MetricState State, string? Detail = null);
