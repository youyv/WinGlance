using WinGlance.Core;

namespace WinGlance.App.Services;

internal static class OverviewSelection
{
    internal static SystemSample Apply(SystemSample source, string? networkId, string? diskId)
    {
        var result = source;
        if (!string.IsNullOrEmpty(networkId))
        {
            var device = source.NetworkInterfaces.FirstOrDefault(x => x.Id == networkId);
            bool ready = device is { IsConnected: true, State: MetricState.Ready };
            result = result with
            {
                NetworkInterface = device is null ? "所选网卡不可用" : device.Name + (ready ? "" : " · " + (device.IsConnected ? "预热 / 不可用" : "已断开")),
                DownloadBytesPerSecond = ready ? device!.DownloadBytesPerSecond : null,
                UploadBytesPerSecond = ready ? device!.UploadBytesPerSecond : null
            };
        }
        if (!string.IsNullOrEmpty(diskId))
        {
            var device = source.PhysicalDisks.FirstOrDefault(x => x.Id == diskId);
            result = result with
            {
                DiskReadBytesPerSecond = device?.State == MetricState.Ready ? device.ReadBytesPerSecond : null,
                DiskWriteBytesPerSecond = device?.State == MetricState.Ready ? device.WriteBytesPerSecond : null
            };
        }
        return result;
    }
}
