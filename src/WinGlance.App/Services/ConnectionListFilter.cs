namespace WinGlance.App.Services;

internal enum ConnectionProtocolFilter { All, Tcp, Udp }
internal enum ConnectionStateFilter { All, Connected, Listening, OtherTcp, UdpBinding }
internal enum ConnectionCopyField { Local, Remote, FullRow }

// Filter only the bounded rows already retrieved. The native query and its source
// coverage, observation timestamp and generation remain unchanged.
internal static class ConnectionListFilter
{
    internal static IReadOnlyList<ProcessConnection> Apply(IReadOnlyList<ProcessConnection> rows,
        ConnectionProtocolFilter protocol, ConnectionStateFilter state)
    {
        if (rows.All(row => Matches(row, protocol, state))) return rows;
        return rows.Where(row => Matches(row, protocol, state)).ToArray();
    }

    internal static bool Matches(ProcessConnection row, ConnectionProtocolFilter protocol, ConnectionStateFilter state)
    {
        bool tcp = row.Protocol is "TCP4" or "TCP6";
        bool udp = row.Protocol is "UDP4" or "UDP6";
        if (protocol == ConnectionProtocolFilter.Tcp && !tcp || protocol == ConnectionProtocolFilter.Udp && !udp) return false;
        return state switch
        {
            ConnectionStateFilter.Connected => tcp && row.State == "已连接",
            ConnectionStateFilter.Listening => tcp && row.State == "监听",
            ConnectionStateFilter.OtherTcp => tcp && row.State is not ("已连接" or "监听"),
            ConnectionStateFilter.UdpBinding => udp,
            _ => true
        };
    }

    internal static string? CopyText(ProcessConnection row, ConnectionCopyField field) => field switch
    {
        ConnectionCopyField.Local => row.LocalEndpoint,
        ConnectionCopyField.Remote => row.RemoteEndpoint,
        _ => $"{row.Protocol}\t{row.State}\t{row.Process.Pid}\t{row.LocalEndpoint}\t{row.RemoteEndpoint ?? "—"}"
    };

    internal static string CountHint(int retrieved, int displayed) => $"已获取 {retrieved} 条中显示 {displayed} 条 · 仅筛选本次取得结果";
}
