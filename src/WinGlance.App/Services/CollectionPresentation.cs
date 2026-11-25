using WinGlance.Core;

namespace WinGlance.App.Services;

// Shared by both pages; rendering a cached snapshot never changes the connection state.
internal sealed record CollectionPresentation(string Label, bool CanEnhance, bool CanReconnect,
    string Live, string Status, string? Notice)
{
    internal static CollectionPresentation Create(CollectorConnection connection, MonitorSnapshot? snapshot, bool paused, DateTimeOffset now)
    {
        bool fresh = connection.Connected && snapshot is not null && now - snapshot.Timestamp <= TimeSpan.FromSeconds(6);
        string label = connection.Connecting ? (connection.Enhanced ? "正在授权…" : "正在连接…") :
            connection.Connected && connection.Enhanced ? "增强采集中" : "增强采集";
        string live = connection.Connecting ? "等待连接" : !connection.Connected ? "采集已断开" :
            !fresh ? "等待新数据" : paused ? "显示已暂停" : "实时 · 1 秒";
        string status = connection.Message;
        if (snapshot is not null)
        {
            string observed = $"{snapshot.Timestamp.LocalDateTime:HH:mm:ss}";
            status = fresh ? $"{observed} 更新 · {snapshot.Processes.Count} 个进程 · {connection.Message}" :
                $"{connection.Message} · 保留最后观测 {observed} 的数据";
        }
        return new(label, !connection.Connecting && !(connection.Connected && connection.Enhanced),
            !connection.Connecting, live, status, connection.Notice);
    }
}
