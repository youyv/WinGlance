using WinGlance.App.Services;
using WinGlance.Core;

internal static class ConnectionListFilterTests
{
    internal static void Run(Action<bool, string> check)
    {
        var identity = new ProcessIdentity(41, 123);
        ProcessConnection[] rows = [
            new("TCP4", identity, "127.0.0.1:100", "127.0.0.1:200", "已连接"),
            new("TCP6", identity, "[fe80::1%7]:100", null, "监听"),
            new("TCP6", identity, "[::1]:100", "[::1]:200", "未知 (99)"),
            new("UDP4", identity, "0.0.0.0:53", null, "本地绑定"),
            new("UDP6", identity, "[fe80::2%13]:53", null, "本地绑定")
        ];
        check(ReferenceEquals(rows, ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.All, ConnectionStateFilter.All)),
            "unfiltered connection view preserves the existing list instance");
        check(ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.Tcp, ConnectionStateFilter.All).Count == 3 &&
            ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.Udp, ConnectionStateFilter.All).Count == 2,
            "connection protocol filtering includes both address families");
        check(ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.All, ConnectionStateFilter.Connected).SequenceEqual([rows[0]]) &&
            ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.Tcp, ConnectionStateFilter.Listening).SequenceEqual([rows[1]]) &&
            ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.Tcp, ConnectionStateFilter.OtherTcp).SequenceEqual([rows[2]]),
            "connection state views distinguish established, listening and unknown TCP states");
        check(ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.Tcp, ConnectionStateFilter.UdpBinding).Count == 0 &&
            ConnectionListFilter.Apply(rows, ConnectionProtocolFilter.Udp, ConnectionStateFilter.Connected).Count == 0,
            "protocol and state filters intersect without fabricating UDP connections");
        check(ConnectionListFilter.CopyText(rows[4], ConnectionCopyField.Local) == "[fe80::2%13]:53" &&
            ConnectionListFilter.CopyText(rows[4], ConnectionCopyField.Remote) is null &&
            ConnectionListFilter.CopyText(rows[1], ConnectionCopyField.FullRow) == "TCP6\t监听\t41\t[fe80::1%7]:100\t—",
            "copy preserves IPv6 scope and distinguishes missing remote endpoints");
        var view = new ConnectionDetailsPresentation();
        view.Select("A", [identity], true, null); view.BindQuery(10);
        var source = new ConnectionReadResult(DateTimeOffset.Now, rows, 2, ["TCP6"], true, 320);
        view.Apply(new(10, "A", source));
        string coverage = view.SourceDescription;
        _ = ConnectionListFilter.Apply(view.Rows, ConnectionProtocolFilter.Udp, ConnectionStateFilter.All);
        check(view.QueryGeneration == 10 && view.Rows.Count == 5 && view.SourceDescription == coverage &&
            coverage.Contains("320") && coverage.Contains("TCP6"), "filtering preserves source count, partial coverage and query generation");
        view.Select("A", [identity], true, "显示已暂停");
        check(view.Hint.Contains(coverage) && view.Rows.Count == 5, "paused historical connections retain source truncation and coverage warnings");
        view.Select("A", [], true, "等待首帧", membershipKnown: false);
        check(view.Rows.Count == 5 && view.Hint.Contains("等待首帧") && !view.CanQuery,
            "a new generation without a fresh frame preserves labelled history instead of declaring app exit");
        view.Select("B", [], true, "等待首帧", membershipKnown: false);
        check(view.Rows.Count == 0 && view.ObservedAt is null && view.SourceDescription == "",
            "unknown membership never carries another application's source coverage");
        view.Select("B", [], true, null);
        check(view.Hint.Contains("已退出"), "a fresh authoritative frame can report a missing application as exited");
    }
}
