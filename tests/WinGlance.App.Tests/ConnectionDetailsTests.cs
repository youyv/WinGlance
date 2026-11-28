using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinGlance.App.Services;
using WinGlance.Core;

internal static class ConnectionDetailsTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        Presentation(check);
        check(IpHelperConnections.Port(0x5000) == 80 && IpHelperConnections.Port(0xbb01) == 443,
            "IP Helper DWORD ports decode their network-order low 16 bits");
        check(IpHelperConnections.Scope(0x03000000) == 3,
            "IPv6 scope IDs decode network byte order");

        var calls = new ConcurrentQueue<(ProcessIdentity[] Identities, CancellationToken Token,
            TaskCompletionSource<ConnectionReadResult> Completion)>();
        var updates = new ConcurrentQueue<ConnectionDetailsSnapshot>();
        var monitor = new ConnectionDetailsMonitor((identities, token) =>
        {
            var completion = new TaskCompletionSource<ConnectionReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            calls.Enqueue((identities.ToArray(), token, completion));
            // Deliberately ignore cancellation until completed to mimic a native call in progress.
            return completion.Task;
        }, TimeSpan.FromMilliseconds(150));
        monitor.Updated += updates.Enqueue;
        ConnectionReadResult Empty() => new(DateTimeOffset.Now, [], 0, [], false, 0);
        var first = new ProcessIdentity(10, 100);
        var second = new ProcessIdentity(20, 200);
        try
        {
            await Task.Delay(200);
            check(calls.Count == 0, "closed details do not query endpoints");
            long generation = monitor.Activate("first", [first]);
            await Until(() => calls.Count == 1);
            var request1 = calls.ElementAt(0);
            check(Enumerable.Range(0, 30).All(_ => monitor.Activate("first", [first]) == generation) && !request1.Token.IsCancellationRequested,
                "ordinary snapshots keep the query generation and in-flight request");
            monitor.Activate("second", [second]);
            check(request1.Token.IsCancellationRequested && calls.Count == 1,
                "selection changes cancel old work while preserving single-flight native queries");
            request1.Completion.SetResult(Empty());
            await Until(() => calls.Count == 2);
            check(updates.Count == 0 && calls.ElementAt(1).Identities.SequenceEqual([second]),
                "late result for the previous application is discarded");
            var request2 = calls.ElementAt(1);
            request2.Completion.SetResult(Empty());
            await Until(() => updates.Count == 1);
            var queued = updates.Single();
            check(monitor.IsCurrent(queued), "current result can be applied by the UI dispatcher");
            long changed = monitor.Activate("second", [second, first]);
            check(!monitor.IsCurrent(queued) && monitor.Activate("second", [first, second, first]) == changed,
                "membership changes invalidate queued UI results but reordering and duplicates do not");
            await Until(() => calls.Count == 3);
            var request3 = calls.ElementAt(2);
            monitor.Deactivate();
            check(request3.Token.IsCancellationRequested, "hiding the details cancels pending endpoint work");
            request3.Completion.SetResult(Empty());
            await Task.Delay(350);
            check(calls.Count == 3 && updates.Count == 1 && !monitor.IsCurrent(queued),
                "hidden view suppresses late results and stops periodic queries");
            monitor.Activate("second", [second]);
            await Until(() => calls.Count == 4);
            calls.ElementAt(3).Completion.SetResult(Empty());
            await Until(() => updates.Count == 2);
            await Until(() => calls.Count == 5);
            check(calls.ElementAt(4).Identities.SequenceEqual([second]), "visible details continue low-frequency refreshes");
            monitor.Dispose();
            check(calls.ElementAt(4).Token.IsCancellationRequested, "disposing the window cancels pending details work");
            calls.ElementAt(4).Completion.SetResult(Empty());
            await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            check(updates.Count == 2 && !monitor.IsCurrent(updates.Last()), "disposed view never publishes another result");
        }
        finally
        {
            monitor.Dispose();
            foreach (var call in calls) call.Completion.TrySetResult(Empty());
            await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }

        if (OperatingSystem.IsWindows()) await NativeLoopback(check);
    }

    private static void Presentation(Action<bool, string> check)
    {
        var first = new ProcessIdentity(10, 100);
        var second = new ProcessIdentity(20, 200);
        var observed = DateTimeOffset.Now;
        var state = new ConnectionDetailsPresentation();
        state.Select("A", [first, second], true, null); state.BindQuery(1);
        var rows = new ProcessConnection[] { new("TCP4", first, "127.0.0.1:80", null, "监听"), new("UDP4", second, "127.0.0.1:90", null, "本地绑定") };
        var update = new ConnectionDetailsSnapshot(1, "A", new(observed, rows, 0, [], false, 2));
        check(state.Apply(update) && state.Rows.Count == 2, "details accept the selected owner's current query");
        var previousRows = state.Rows;
        state.Select("A", [second, first], true, null); state.BindQuery(1); state.Apply(update);
        check(ReferenceEquals(previousRows, state.Rows), "same membership and equal rows preserve the list binding");
        state.Select("A", [first, second], true, "连接详情已暂停");
        check(state.Rows.Count == 2 && state.Hint.Contains("上次观测") && !state.Apply(update), "pause retains labelled history but rejects queued updates");
        state.Select("B", [second], true, "采集已断开");
        check(state.OwnerKey == "B" && state.Rows.Count == 0 && state.ObservedAt is null && !state.Apply(update), "paused or disconnected selection cannot retain another application's rows or timestamp");
        state.Select("A", [first, second], true, null); state.BindQuery(2);
        state.Apply(update with { Generation = 2 });
        state.Select("A", [second], true, null); state.BindQuery(3);
        check(state.Rows.Count == 1 && state.Rows[0].Process == second && state.Hint.Contains("更新中") &&
            !state.Apply(update with { Generation = 2 }), "membership change retains only surviving identities and rejects the old query");
        state.Select("A", [second with { StartTimeUtcTicks = 201 }], true, null);
        check(state.Rows.Count == 0, "PID reuse does not keep old process endpoints");
        state.Select("A", [first], false, null);
        check(state.OwnerKey is null && state.Rows.Count == 0 && state.ObservedAt is null, "closing or hiding details clears their owner");
        check(StartupRegistration.TryParseRegisteredCommand("  \"C:\\Old Folder\\WinGlance.App.exe\" --minimized  ", out var path, out var minimized) &&
            path == @"C:\Old Folder\WinGlance.App.exe" && minimized, "moved executable command retains its silent startup preference");
        check(StartupRegistration.TryParseRegisteredCommand("\"D:\\New\\WinGlance.App.exe\"", out _, out minimized) && !minimized,
            "ordinary startup remains distinguishable from silent startup");
        check(new[] { "WinGlance.App.exe --minimized", "\"relative\\WinGlance.App.exe\"", "\"C:\\x.exe\" --minimized", "\"C:\\WinGlance.App.exe\" --minimized extra", "\"C:\\WinGlance.App.exe\"--minimized" }
            .All(command => !StartupRegistration.TryParseRegisteredCommand(command, out _, out _)), "startup parser rejects unknown programs, arguments and malformed quoting");
    }

    private static async Task NativeLoopback(Action<bool, string> check)
    {
        using var process = Process.GetCurrentProcess();
        var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);
        using var listener4 = new TcpListener(IPAddress.Loopback, 0);
        listener4.Start();
        using var client4 = new TcpClient(AddressFamily.InterNetwork);
        await client4.ConnectAsync((IPEndPoint)listener4.LocalEndpoint);
        using var accepted4 = await listener4.AcceptTcpClientAsync();
        using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var result = await IpHelperConnections.ReadAsync([identity], CancellationToken.None);
        string server4 = listener4.LocalEndpoint.ToString()!, local4 = client4.Client.LocalEndPoint!.ToString()!;
        check(result.Connections.Any(x => x.Protocol == "TCP4" && x.LocalEndpoint == local4 && x.RemoteEndpoint == server4 && x.State == "已连接"),
            "native IPv4 table reports a controlled TCP connection with both endpoints");
        check(result.Connections.Any(x => x.Protocol == "TCP4" && x.LocalEndpoint == server4 && x.RemoteEndpoint is null && x.State == "监听"),
            "TCP listener has no fabricated remote endpoint");
        check(result.Connections.Any(x => x.Protocol == "UDP4" && x.LocalEndpoint == udp4.Client.LocalEndPoint!.ToString() && x.RemoteEndpoint is null && x.State == "本地绑定"),
            "native UDP table reports only the controlled local binding");
        check(result.UnverifiedProcesses == 0 && result.UnavailableTables.Count == 0 && !result.Truncated &&
              result.Connections.All(x => x.Process == identity) && result.MatchedCount == result.Connections.Count,
            "endpoint attribution retains the exact process creation identity and complete count");

        if (Socket.OSSupportsIPv6)
        {
            using var listener6 = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener6.Start();
            using var client6 = new TcpClient(AddressFamily.InterNetworkV6);
            await client6.ConnectAsync((IPEndPoint)listener6.LocalEndpoint);
            using var accepted6 = await listener6.AcceptTcpClientAsync();
            using var udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            var ipv6 = await IpHelperConnections.ReadAsync([identity], CancellationToken.None);
            check(ipv6.Connections.Any(x => x.Protocol == "TCP6" && x.LocalEndpoint == client6.Client.LocalEndPoint!.ToString() &&
                  x.RemoteEndpoint == listener6.LocalEndpoint.ToString() && x.State == "已连接"),
                "native IPv6 table preserves bracketed TCP endpoints");
            check(ipv6.Connections.Any(x => x.Protocol == "UDP6" && x.LocalEndpoint == udp6.Client.LocalEndPoint!.ToString() && x.RemoteEndpoint is null),
                "native IPv6 UDP table reports the controlled binding");
        }

        var reused = await IpHelperConnections.ReadAsync([identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }], CancellationToken.None);
        check(reused.Connections.Count == 0 && reused.UnverifiedProcesses == 1,
            "wrong creation time cannot attribute connections to a reused PID");
        var missing = await IpHelperConnections.ReadAsync([new(identity.Pid, 0)], CancellationToken.None);
        check(missing.Connections.Count == 0 && missing.UnverifiedProcesses == 1,
            "missing process creation identity stays unverified");
        var conflicted = await IpHelperConnections.ReadAsync([identity, identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }], CancellationToken.None);
        check(conflicted.Connections.Count == 0 && conflicted.UnverifiedProcesses == 2,
            "conflicting generations of one PID are never guessed");
        var manyBindings = new List<UdpClient>();
        try
        {
            for (int i = 0; i <= IpHelperConnections.MaximumConnections; i++)
                manyBindings.Add(new(new IPEndPoint(IPAddress.Loopback, 0)));
            var bounded = await IpHelperConnections.ReadAsync([identity], CancellationToken.None);
            check(bounded.Truncated && bounded.Connections.Count == IpHelperConnections.MaximumConnections &&
                  bounded.MatchedCount > IpHelperConnections.MaximumConnections,
                "large native endpoint sets retain a bounded display and explicit truncation");
        }
        finally { foreach (var binding in manyBindings) binding.Dispose(); }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool sawCancellation = false;
        try { await IpHelperConnections.ReadAsync([identity], cancelled.Token); }
        catch (OperationCanceledException) { sawCancellation = true; }
        check(sawCancellation, "cancelled endpoint requests do not query native tables");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
