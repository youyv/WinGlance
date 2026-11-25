using System.ComponentModel;
using System.Diagnostics;
using WinGlance.App.Services;

internal static class CollectionRegression
{
    internal static async Task Run(Action<bool, string> check)
    {
        var snapshot = Snapshots.Create(false);
        var connected = new CollectorConnection(false, true, false, "普通采集中", "未能授权");
        foreach (bool paused in new[] { false, true })
        {
            var view = CollectionPresentation.Create(connected, snapshot, paused, snapshot.Timestamp);
            check(view.Notice == "未能授权" && view.CanEnhance && view.CanReconnect,
                $"connection notice survives rendering and pause state {paused}");
            var disconnected = CollectionPresentation.Create(connected with { Connected = false, Message = "已断开" }, snapshot, paused, snapshot.Timestamp);
            check(disconnected.Live == "采集已断开" && disconnected.Status.Contains("最后观测"),
                $"cached values stay labelled as last observed on page/tray render with pause state {paused}");
        }
        check(CollectionPresentation.Create(connected, snapshot, false, snapshot.Timestamp.AddSeconds(10)).Live == "等待新数据",
            "old frame cannot be labelled live even while a pipe is connected");
        var waiting = CollectionPresentation.Create(connected with { Connecting = true, Connected = false }, snapshot, false, snapshot.Timestamp);
        check(!waiting.CanReconnect && !waiting.CanEnhance && waiting.Live == "等待连接", "both pages disable repeated connection actions during handover");

        using (var launcher = new FakeLauncher())
        {
            int elevationAttempts = 0;
            using var client = new CollectorClient(launcher.ExistingPath, () => true, (info, token) =>
            {
                if (info.Verb != "runas") return launcher.Launch(info, token);
                Interlocked.Increment(ref elevationAttempts);
                return Task.FromException<Process?>(new Win32Exception(5));
            });
            var ordinary = client.StartAsync();
            await Until(() => client.Connection.Connected);
            var enhancement = client.StartAsync(true);
            await Until(() => client.Connection.Connected && launcher.Launches == 2);
            await ordinary.WaitAsync(TimeSpan.FromSeconds(5));
            check(!client.Connection.Enhanced && client.Connection.Notice is not null && elevationAttempts == 1,
                "failed ordinary-to-enhanced handover restores ordinary once without another UAC request");
            client.Dispose();
            await enhancement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var launcher = new FakeLauncher())
        {
            bool cancel = false;
            int elevations = 0;
            using var client = new CollectorClient(launcher.ExistingPath, () => true, (info, token) =>
            {
                if (info.Verb == "runas")
                {
                    elevations++;
                    if (cancel) return Task.FromException<Process?>(new Win32Exception(1223));
                }
                return launcher.Launch(info, token);
            });
            var enhanced = client.StartAsync(true);
            await Until(() => client.Connection.Connected);
            launcher.Peers[0].Dispose();
            await enhanced.WaitAsync(TimeSpan.FromSeconds(5));
            cancel = true;
            var retry = client.StartAsync(true);
            await Until(() => client.Connection.Connected);
            check(!client.Connection.Enhanced && client.Connection.Notice!.Contains("已取消") && elevations == 2,
                "cancelled enhancement after an enhanced disconnect recovers ordinary collection without repeated UAC");
            client.Dispose();
            await retry.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var launcher = new FakeLauncher())
        {
            var pending = new TaskCompletionSource<Process?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int attempts = 0;
            using var client = new CollectorClient(launcher.ExistingPath, () => true, (_, _) => { attempts++; return pending.Task; });
            var run = client.StartAsync(true);
            client.Dispose();
            pending.SetException(new Win32Exception(1223));
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            check(attempts == 1, "application disposal during UAC prevents fallback resurrection");
        }
        using (var launcher = new FakeLauncher())
        {
            int attempts = 0;
            using var client = new CollectorClient(launcher.ExistingPath, () => true, (_, _) =>
            {
                attempts++;
                return Task.FromException<Process?>(new Win32Exception(5));
            });
            await client.StartAsync(true).WaitAsync(TimeSpan.FromSeconds(5));
            check(attempts == 2 && !client.Connection.Connected && client.Connection.Notice is not null,
                "failed fallback reports disconnected state and never loops");
        }
        using (var launcher = new FakeLauncher())
        using (var client = new CollectorClient(launcher.ExistingPath, () => true, (_, _) =>
            Task.FromResult(Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 7")
                { UseShellExecute = false, CreateNoWindow = true }))))
        {
            var watch = Stopwatch.StartNew();
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(watch.Elapsed < TimeSpan.FromSeconds(5) && client.Connection.Message.Contains("已退出"),
                "dedicated test child exiting before pipe connect is detected promptly");
        }
        using (var launcher = new FakeLauncher())
        {
            FakePeer? silentPeer = null;
            using var client = new CollectorClient(launcher.ExistingPath, () => true, async (info, token) =>
            {
                var arguments = info.Arguments.Split(' ');
                silentPeer = new FakePeer(arguments[Array.IndexOf(arguments, "--pipe") + 1]);
                await silentPeer.Connect(token);
                return Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 7")
                    { UseShellExecute = false, CreateNoWindow = true });
            });
            try
            {
                await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check(!client.Connection.Connected && client.Connection.Message.Contains("已退出"),
                    "child exit is observed after pipe connect while waiting for the first snapshot");
            }
            finally { silentPeer?.Dispose(); }
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
