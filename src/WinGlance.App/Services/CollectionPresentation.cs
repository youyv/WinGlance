using WinGlance.Core;

namespace WinGlance.App.Services;

// Shared by both pages; rendering a cached snapshot never changes the connection state.
internal sealed record CollectionPresentation(string Label, bool CanEnhance, bool CanReconnect,
    string Live, string Status, string? Notice, bool IsLive, string CompactStatus, DateTimeOffset? LastObservedUtc)
{
    // A paused overview owns its displayed timestamp, but never owns connection freshness.
    internal CollectionPresentation WithOverviewDisplay(bool paused, LiveFrame? displayedFrame)
    {
        if (!paused) return this;
        string displayed = displayedFrame is { } frame
            ? $"展示停留在 {frame.Snapshot.Timestamp.LocalDateTime:HH:mm:ss} · {frame.Snapshot.Processes.Count} 个进程"
            : "暂无已展示数据";
        return this with
        {
            Live = IsLive ? "显示已暂停" : Live,
            Status = IsLive ? displayed + " · 显示已暂停，后台采集继续" : Status + " · " + displayed + "（显示已暂停）"
        };
    }

    internal static CollectionPresentation Create(CollectorConnection connection, LiveFrame? frame, bool paused)
    {
        var snapshot = frame?.Snapshot;
        bool fresh = !connection.Connecting && connection.Connected && connection.Freshness == CollectionFreshness.Fresh &&
            frame is not null && frame.Generation == connection.Generation;
        string label = connection.Connecting ? (connection.Enhanced ? "正在授权…" : "正在连接…") :
            connection.Connected && connection.Enhanced ? "增强采集中" : "增强采集";
        string collection = connection.Connecting ? "等待连接" : !connection.Connected ? "采集已断开" :
            !fresh ? "等待新数据" : "实时 · 1 秒";
        string live = fresh && paused ? "显示已暂停" : collection;
        string status = connection.Message;
        DateTimeOffset? lastObserved = snapshot?.Timestamp ?? connection.LastObservedUtc;
        if (snapshot is not null)
        {
            string observed = $"{snapshot.Timestamp.LocalDateTime:HH:mm:ss}";
            status = fresh ? $"{observed} 更新 · {snapshot.Processes.Count} 个进程 · {connection.Message}" :
                $"{connection.Message} · 保留最后观测 {observed} 的数据";
        }
        string compact = fresh ? "实时" : collection + (lastObserved is { } observedAt ? $" · 最后观测 {observedAt.LocalDateTime:HH:mm:ss}" : "");
        return new(label, !connection.Connecting && !(connection.Connected && connection.Enhanced),
            !connection.Connecting, live, status, connection.Notice, fresh, compact, lastObserved);
    }
}
