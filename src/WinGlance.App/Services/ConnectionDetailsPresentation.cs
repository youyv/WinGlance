using WinGlance.Core;

namespace WinGlance.App.Services;

// UI-owned state. The displayed owner's identity is changed before deciding whether querying
// is possible; cancellation of a reader alone cannot remove already displayed rows.
internal sealed class ConnectionDetailsPresentation
{
    private ProcessIdentity[] _members = [];
    public string? OwnerKey { get; private set; }
    public long MembershipRevision { get; private set; }
    public long QueryGeneration { get; private set; } = -1;
    public DateTimeOffset? ObservedAt { get; private set; }
    public IReadOnlyList<ProcessConnection> Rows { get; private set; } = [];
    public bool CanQuery { get; private set; }
    public string Hint { get; private set; } = "展开详情时按需查询";
    public string EmptyMessage { get; private set; } = "";

    public void Select(string? owner, IEnumerable<ProcessIdentity>? identities, bool visible, string? unavailable)
    {
        if (!visible || owner is null) { Clear(); return; }
        if (!string.Equals(OwnerKey, owner, StringComparison.Ordinal)) { Clear(); OwnerKey = owner; }
        var members = identities?.Distinct().OrderBy(x => x.Pid).ThenBy(x => x.StartTimeUtcTicks).ToArray() ?? [];
        bool changed = !_members.SequenceEqual(members);
        if (changed)
        {
            _members = members; MembershipRevision++; QueryGeneration = -1;
            var retained = Rows.Where(row => Array.IndexOf(members, row.Process) >= 0).ToArray();
            if (retained.Length != Rows.Count) Rows = retained;
        }
        bool wasQuerying = CanQuery;
        CanQuery = unavailable is null && members.Length > 0;
        if (members.Length == 0)
        {
            Rows = []; ObservedAt = null; QueryGeneration = -1;
            Hint = "应用已退出"; EmptyMessage = "当前没有可查询的进程";
        }
        else if (unavailable is not null)
        {
            QueryGeneration = -1;
            Hint = unavailable + HistorySuffix;
            EmptyMessage = "恢复实时状态后查询当前应用连接";
        }
        else if (changed || !wasQuerying)
        {
            Hint = (ObservedAt is null ? "正在查询连接…" : "更新中") + HistorySuffix;
            EmptyMessage = "正在查询连接…";
        }
    }

    public void BindQuery(long generation) { if (CanQuery) QueryGeneration = generation; }

    public bool Apply(ConnectionDetailsSnapshot update)
    {
        if (!CanQuery || update.SelectionKey != OwnerKey || update.Generation != QueryGeneration) return false;
        var result = update.Result;
        if (result.Connections.Count == 0 && result.MatchedCount == 0 &&
            result.IsPartial && ObservedAt is not null)
        {
            Hint = "本次查询未完整读取 · " + result.Description + HistorySuffix;
            EmptyMessage = "部分连接无法读取，详见上方提示";
            return true;
        }
        if (!Rows.SequenceEqual(result.Connections)) Rows = result.Connections;
        ObservedAt = result.ObservedAt;
        Hint = result.Description;
        EmptyMessage = result.EmptyMessage;
        return true;
    }

    public void Clear()
    {
        OwnerKey = null; _members = []; Rows = []; ObservedAt = null; QueryGeneration = -1;
        CanQuery = false; Hint = "展开详情时按需查询"; EmptyMessage = "";
    }

    private string HistorySuffix => ObservedAt is { } observed ? $" · 上次观测 {observed.LocalDateTime:HH:mm:ss}" : "";
}
