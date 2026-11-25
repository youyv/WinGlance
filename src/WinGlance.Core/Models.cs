namespace WinGlance.Core;

public enum MetricState { Ready, WarmingUp, PermissionRequired, Unsupported, Error }
public enum SoftwareCategory { Application, ThirdPartyService, WindowsSystem, Unknown }
public sealed record Capability(MetricState State, string Message);
public sealed record ProcessIdentity(int Pid, long StartTimeUtcTicks);
public sealed record SoftwareIdentity(string Key, string DisplayName, string ProcessName,
    string? FilePath, string? Publisher, SoftwareCategory Category, string ClassificationReason,
    string? IconPath = null);
public sealed record ProcessSample(
    ProcessIdentity Identity, SoftwareIdentity Software, double? CpuPercent, long? PrivateWorkingSetBytes,
    double? DownloadBytesPerSecond, double? UploadBytesPerSecond,
    double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond,
    IReadOnlyDictionary<string, double> GpuEngines, long? DedicatedGpuBytes, long? SharedGpuBytes,
    MetricState GpuState = MetricState.Ready);
public sealed record SystemSample(double? CpuPercent, long TotalMemoryBytes, long AvailableMemoryBytes,
    double? DownloadBytesPerSecond, double? UploadBytesPerSecond,
    double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond, double? GpuPercent,
    string NetworkInterface)
{
    public IReadOnlyList<NetworkInterfaceSample> NetworkInterfaces { get; init; } = [];
    public IReadOnlyList<PhysicalDiskSample> PhysicalDisks { get; init; } = [];
    public string? DefaultNetworkInterfaceId { get; init; }
}
public sealed record MonitorSnapshot(DateTimeOffset Timestamp, double IntervalSeconds,
    SystemSample System, IReadOnlyList<ProcessSample> Processes,
    IReadOnlyDictionary<string, Capability> Capabilities, bool IsElevated)
{
    public AccountingFrame? Accounting { get; init; }
}
public sealed record SoftwareSample(string Key, SoftwareIdentity Software, IReadOnlyList<ProcessSample> Processes,
    double? CpuPercent, long? PrivateWorkingSetBytes, double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond, double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond,
    double? GpuPercent, long? DedicatedGpuBytes, long? SharedGpuBytes);
public sealed record HistoryPoint(DateTimeOffset Timestamp, double? CpuPercent, double? GpuPercent,
    double? DownloadBytesPerSecond, double? UploadBytesPerSecond);

public interface IProcessIdentityResolver
{
    SoftwareIdentity Resolve(int pid, string processName, string? filePath, int sessionId, long startTimeUtcTicks = 0);
}
