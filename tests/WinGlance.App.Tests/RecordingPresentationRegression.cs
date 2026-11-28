using WinGlance.App.Services;
using WinGlance.Statistics;

internal static class RecordingPresentationRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var snapshot = Snapshots.Create(false);
        var frozen = new LiveFrame(snapshot with { Timestamp = snapshot.Timestamp.AddMinutes(-1) }, [], 1);
        foreach (bool enhanced in new[] { false, true })
        {
            var connection = new CollectorConnection(false, true, enhanced, enhanced ? "增强采集中" : "普通采集中",
                "保留授权提示", Generation: 2, Freshness: CollectionFreshness.Fresh);
            var current = new LiveFrame(snapshot, [], 2);
            var shared = CollectionPresentation.Create(connection, current, false);
            var overview = shared.WithOverviewDisplay(true, frozen);
            check(overview.Live == "显示已暂停" && shared.Live == "实时 · 1 秒" && overview.IsLive,
                $"paused overview uses current generation after reconnect/enhancement ({enhanced})");
            check(overview.Status.Contains($"{frozen.Snapshot.Timestamp.LocalDateTime:HH:mm:ss}") &&
                overview.Status.Contains("展示停留") && overview.Notice == shared.Notice &&
                overview.CanEnhance == shared.CanEnhance && overview.CanReconnect == shared.CanReconnect &&
                overview.CompactStatus == shared.CompactStatus && overview.LastObservedUtc == shared.LastObservedUtc,
                $"frozen presentation affects only overview Live/Status ({enhanced})");
            var later = CollectionPresentation.Create(connection, current with
            { Snapshot = snapshot with { Timestamp = snapshot.Timestamp.AddSeconds(4) } }, false);
            check(later.WithOverviewDisplay(true, frozen).Status == overview.Status,
                $"paused displayed timestamp remains stable while current frames advance ({enhanced})");
            check(ReferenceEquals(shared.WithOverviewDisplay(false, frozen), shared),
                $"resumed/page/tray render uses shared state ({enhanced})");
            check(shared.WithOverviewDisplay(true, null).Status.Contains("暂无已展示数据"),
                $"pause before first render does not pretend a current frame is displayed ({enhanced})");
            foreach (var unavailable in new[]
            {
                connection with { Connecting = true, Connected = false },
                connection with { Connected = false },
                connection with { Freshness = CollectionFreshness.Stalled },
                connection with { Generation = 3 }
            })
            {
                var state = CollectionPresentation.Create(unavailable, current, false);
                var paused = state.WithOverviewDisplay(true, frozen);
                check(!paused.IsLive && paused.Live == state.Live && paused.Live != "显示已暂停",
                    $"unavailable channel takes priority over paused display ({enhanced}, {state.Live})");
            }
        }

        var live = CollectionPresentation.Create(new(false, true, false, "普通采集中", Generation: 1,
            Freshness: CollectionFreshness.Fresh), new(snapshot, [], 1), false);
        foreach (var phase in new[] { StatisticsRecordingPhase.WaitingForFirstInterval,
            StatisticsRecordingPhase.WaitingAfterReset, StatisticsRecordingPhase.WaitingForBaseline })
        {
            var waiting = StatisticsNoticePresentation.Create("已有缺测覆盖提示", false, live, new(phase, TimeSpan.FromSeconds(6), null));
            check(!waiting.IsWarning && waiting.Message.Contains("等待") && waiting.Message.Contains("已有缺测覆盖提示") &&
                !waiting.Message.Contains("未推进"), $"{phase} receives bounded informational warmup");
            var late = StatisticsNoticePresentation.Create("已有缺测覆盖提示", false, live, new(phase, TimeSpan.FromSeconds(6.001), null));
            check(late.IsWarning && late.Message.Contains("统计超过 6 秒未入账") && late.Message.Contains("通道实时"),
                $"{phase} cannot hide stalled accounting behind fresh frames");
        }
        var recording = new StatisticsRecordingProgress(StatisticsRecordingPhase.Recording, TimeSpan.FromSeconds(7), snapshot.Timestamp);
        var warning = StatisticsNoticePresentation.Create("存储失败\n统计队列已满\n流量覆盖不足", true, live, recording);
        check(warning.IsWarning && warning.Message.Contains("存储失败") && warning.Message.Contains("队列已满") &&
            warning.Message.Contains("覆盖不足") && warning.Message.Contains("统计入账暂未推进"),
            "runtime progress preserves storage, queue and missing-coverage warnings");
        var disconnected = live with { IsLive = false, Live = "采集已断开", CompactStatus = "采集已断开 · 最后观测 12:00:00" };
        var stopped = StatisticsNoticePresentation.Create(null, false, disconnected, recording);
        check(stopped.Message.Contains("采集通道：采集已断开") && stopped.Message.Contains("统计超过 6 秒未入账") &&
            !stopped.Message.Contains("通道实时"), "disconnected channel and accounting stall remain distinct");
        check(StatisticsNoticePresentation.Create(null, false, live, recording with { Age = TimeSpan.Zero }).Message.Length == 0,
            "successful interval clears runtime notice without changing coverage state");
    }
}
