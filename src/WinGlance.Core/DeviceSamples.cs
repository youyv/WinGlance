namespace WinGlance.Core;

public sealed record NetworkInterfaceSample(string Id, string Name,
    double? DownloadBytesPerSecond, double? UploadBytesPerSecond,
    MetricState State, bool IsPhysical, bool IsConnected, string? Detail = null);

/// <summary>Id is the PDH physical-disk instance name and can change after repartitioning.</summary>
public sealed record PhysicalDiskSample(string Id, string Name,
    double? ReadBytesPerSecond, double? WriteBytesPerSecond, MetricState State, string? Detail = null);
