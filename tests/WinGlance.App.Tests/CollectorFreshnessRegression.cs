using System.Collections;
using System.Collections.Concurrent;
using WinGlance.App.Services;
using WinGlance.Core;
using WinGlance.Statistics;

internal static class CollectorFreshnessRegression
{
    internal static async Task Run(Action<bool, string> check)
    {
        using (var launcher = new FakeLauncher())
        {
            var clock = new CollectionTestClock();
            using var client = new CollectorClient(launcher.ExistingPath, () => true, launcher.Launch, clock);
            int notifications = 0, badFirstState = 0;
            client.ConnectionChanged += () =>
            {
                Interlocked.Increment(ref notifications);
                var value = client.ReadState();
                if (value.Connection.Connected && value.Frame?.Generation != value.Connection.Generation)
                    Interlocked.Increment(ref badFirstState);
            };
            Task run = client.StartAsync();
            await Until(() => client.Connection.Connected);
            long generation = client.Connection.Generation;
            var timer = clock.Timers[0];
            check(badFirstState == 0 && client.ReadState().Frame?.Generation == generation,
                "first connected notification observes an already committed frame in the same generation");
            int baseline = notifications;
            clock.Advance(TimeSpan.FromMilliseconds(5999));
            check(client.Connection.Freshness == CollectionFreshness.Fresh && notifications == baseline,
                "no heartbeat warning before the six-second monotonic deadline");
            clock.SetUtc(clock.GetUtcNow().AddDays(3));
            check(client.Connection.Freshness == CollectionFreshness.Fresh,
                "wall-clock forward jump does not make a healthy collection stale");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            check(client.Connection.Connected && client.Connection.Freshness == CollectionFreshness.Stalled && notifications == baseline + 1,
                "open pipe without new frames publishes one stale transition without user interaction");
            clock.Advance(TimeSpan.FromMinutes(1));
            check(notifications == baseline + 1 && !timer.IsArmed && launcher.Launches == 1,
                "stalled collection stops its one-shot timer and never relaunches or requests UAC");
            var backwards = Snapshots.Create(false, 2) with { Timestamp = clock.GetUtcNow().AddDays(-10) };
            await launcher.Peers[0].Send(backwards);
            await Until(() => client.ReadState().Frame?.Snapshot.IntervalSeconds == 2);
            check(client.Connection.Freshness == CollectionFreshness.Fresh && client.ReadState().Frame?.Snapshot.Timestamp == backwards.Timestamp,
                "fresh compatible frame restores collection even after a wall-clock rollback");
            var view = CollectionPresentation.Create(client.Connection, client.ReadState().Frame, true);
            check(view.Live == "显示已暂停" && view.IsLive && view.CompactStatus == "实时",
                "pausing the main view does not pause collection freshness or floating display status");
            var mismatch = CollectionPresentation.Create(client.Connection with { Generation = generation + 1 }, client.ReadState().Frame, false);
            check(!mismatch.IsLive && mismatch.Live == "等待新数据" && mismatch.CompactStatus.Contains("最后观测"),
                "fresh connection state cannot relabel a prior generation frame as live");

            var collectorRun = Guid.NewGuid();
            var sequenced = Frame(collectorRun, 1, clock.GetUtcNow());
            await launcher.Peers[0].Send(sequenced);
            await Until(() => client.ReadState().Frame?.Snapshot.Accounting?.Sequence == 1);
            clock.Advance(TimeSpan.FromSeconds(5));
            check(!client.TryAcceptObservation(generation, sequenced, out _, out _) &&
                  !client.TryAcceptObservation(generation, Frame(collectorRun, 0, clock.GetUtcNow()), out _, out _) &&
                  !client.TryAcceptObservation(generation, Frame(Guid.NewGuid(), 2, clock.GetUtcNow()), out _, out _),
                "duplicate, reversed and unrelated collector run sequences cannot renew current progress");
            clock.Advance(TimeSpan.FromSeconds(1));
            check(client.Connection.Freshness == CollectionFreshness.Stalled,
                "repeated snapshots do not keep an otherwise stalled pipe live");
            await launcher.Peers[0].Send(Frame(collectorRun, 2, clock.GetUtcNow()));
            await Until(() => client.ReadState().Frame?.Snapshot.Accounting?.Sequence == 2);
            clock.Advance(TimeSpan.FromSeconds(5));
            await launcher.Peers[0].Send(Frame(collectorRun, 3, clock.GetUtcNow()));
            await Until(() => client.ReadState().Frame?.Snapshot.Accounting?.Sequence == 3);
            clock.Advance(TimeSpan.FromSeconds(1));
            check(client.Connection.Freshness == CollectionFreshness.Fresh && timer.IsArmed,
                "scheduled old deadline rechecks new progress and rearms for the remaining duration");
            client.OnSuspending();
            client.OnSuspending();
            clock.Advance(TimeSpan.FromHours(2));
            check(client.Connection.Freshness == CollectionFreshness.WaitingAfterResume && !timer.IsArmed,
                "suspension stops the timer and keeps last data explicitly waiting");
            client.OnResumed();
            client.OnResumed();
            check(client.Connection.Freshness == CollectionFreshness.WaitingAfterResume,
                "duplicate resume notifications cannot invent fresh data");
            await launcher.Peers[0].Send(Frame(collectorRun, 4, clock.GetUtcNow()));
            await Until(() => client.ReadState().Frame?.Snapshot.Accounting?.Sequence == 4);
            client.OnResumed();
            check(client.Connection.Freshness == CollectionFreshness.Fresh,
                "a late duplicate resume does not undo freshness restored by a real frame");
            client.OnSuspending();
            await launcher.Peers[0].Send(Frame(collectorRun, 5, clock.GetUtcNow()));
            await Until(() => client.ReadState().Frame?.Snapshot.Accounting?.Sequence == 5);
            client.OnResumed();
            check(client.Connection.Freshness == CollectionFreshness.Fresh && timer.IsArmed,
                "a new frame received before resume notification remains fresh afterward");

            Task replacement = client.StartAsync(false, true);
            await Until(() => client.Connection.Connected && client.Connection.Generation != generation);
            var current = client.ReadState();
            timer.FireQueuedCallback();
            check(client.ReadState() == current,
                "an already queued timer callback from a replaced generation has no effect");
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            var currentTimer = clock.Timers[^1];
            var lastFrame = client.ReadState().Frame;
            client.Dispose();
            currentTimer.FireQueuedCallback();
            clock.Advance(TimeSpan.FromHours(1));
            check(!client.TryAcceptObservation(current.Connection.Generation, Frame(Guid.NewGuid(), 99, clock.GetUtcNow()), out _, out _) &&
                  client.ReadState().Frame == lastFrame && !client.Connection.Connected,
                "disposal rejects further admission and all queued freshness callbacks");
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using (var launcher = new FakeLauncher())
        {
            var clock = new CollectionTestClock();
            using var client = new CollectorClient(launcher.ExistingPath, () => false, launcher.Launch, clock);
            await client.StartAsync(true);
            check(client.Connection.Notice!.Contains("恢复普通采集") && !client.Connection.Notice.Contains("继续运行"),
                "unsupported-account notice accurately describes disconnected collection");
            Task run = client.StartAsync();
            await Until(() => client.Connection.Connected);
            clock.Advance(TimeSpan.FromSeconds(6));
            await client.StartAsync(true);
            check(client.Connection.Notice!.Contains("暂未更新") && !client.Connection.Notice.Contains("继续运行"),
                "unsupported-account notice distinguishes stalled ordinary collection");
            client.Dispose(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        }

        string directory = Path.Combine(Path.GetTempPath(), "WinGlance-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var statistics = new StatisticsService(Path.Combine(directory, "statistics.db"));
            using var launcher = new FakeLauncher();
            var live = new LiveSnapshotStore();
            using var client = new CollectorClient(launcher.ExistingPath, () => true, launcher.Launch,
                new CollectionTestClock(), live, statistics);
            Task oldRun = client.StartAsync();
            await Until(() => client.Connection.Connected);
            long oldGeneration = client.Connection.Generation;
            using (var barrier = new BlockingProcesses())
            {
                var blocked = Frame(Guid.NewGuid(), 1, DateTimeOffset.UtcNow) with { Processes = barrier };
                Task<bool> oldAdmission = Task.Run(() => client.TryAcceptObservation(oldGeneration, blocked, out _, out _));
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Task replacement = client.StartAsync(false, true);
                await Until(() => client.Connection.Connected && client.Connection.Generation != oldGeneration);
                barrier.Release.Set();
                check(!await oldAdmission.WaitAsync(TimeSpan.FromSeconds(5)) && live.Latest?.Generation == client.Connection.Generation,
                    "old frame prepared across a connection handover cannot commit into the new live generation");
                var totals = await statistics.QueryAsync(StatisticsRange.CurrentSession);
                check(totals.LastRecordedUtc is null,
                    "old frame rejected at the admission gate never reaches the statistics queue");
                await oldRun.WaitAsync(TimeSpan.FromSeconds(5));
                oldRun = replacement;
            }
            using (var barrier = new BlockingProcesses())
            {
                long generation = client.Connection.Generation;
                var blocked = Frame(Guid.NewGuid(), 1, DateTimeOffset.UtcNow) with { Processes = barrier };
                Task<bool> pending = Task.Run(() => client.TryAcceptObservation(generation, blocked, out _, out _));
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var last = live.Latest;
                client.Dispose();
                barrier.Release.Set();
                check(!await pending.WaitAsync(TimeSpan.FromSeconds(5)) && live.Latest == last,
                    "a frame paused during preparation cannot commit after Dispose returns");
                check((await statistics.QueryAsync(StatisticsRange.CurrentSession)).LastRecordedUtc is null,
                    "disposing during preparation prevents late statistics admission");
            }
            await oldRun.WaitAsync(TimeSpan.FromSeconds(5));
            await statistics.DisposeAsync();
            using var rejectedLauncher = new FakeLauncher();
            using var rejected = new CollectorClient(rejectedLauncher.ExistingPath, () => true, rejectedLauncher.Launch,
                new CollectionTestClock(), statistics: statistics);
            Task rejectedRun = rejected.StartAsync();
            await Until(() => rejected.Connection.Connected);
            await rejectedLauncher.Peers[0].Send(Frame(Guid.NewGuid(), 1, DateTimeOffset.UtcNow));
            await Until(() => rejected.ReadState().Frame?.Snapshot.Accounting?.Sequence == 1);
            check(rejected.Connection.Freshness == CollectionFreshness.Fresh && rejected.ReadState().Frame is not null,
                "statistics queue rejection cannot block live publication or mark the channel stale");
            rejected.Dispose(); await rejectedRun.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static MonitorSnapshot Frame(Guid run, long sequence, DateTimeOffset end) => Snapshots.Create(false) with
    {
        Timestamp = end,
        Accounting = new(run, sequence, end.AddSeconds(-1), end, 1, [],
            new(new Dictionary<StatisticMetric, MetricAccounting> { [StatisticMetric.Cpu] = new(10, 1, 10) }))
    };
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    private sealed class BlockingProcesses : IReadOnlyList<ProcessSample>, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public int Count => 0;
        public ProcessSample this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<ProcessSample> GetEnumerator()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("preparation barrier");
            return Enumerable.Empty<ProcessSample>().GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}

internal sealed class CollectionTestClock : TimeProvider
{
    private readonly object _sync = new();
    private long _timestamp;
    private long _utc = new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    internal List<TestTimer> Timers { get; } = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utc), TimeSpan.Zero);
    internal void SetUtc(DateTimeOffset value) => Interlocked.Exchange(ref _utc, value.UtcTicks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        lock (_sync) Timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    internal void Advance(TimeSpan interval)
    {
        Interlocked.Add(ref _timestamp, interval.Ticks); Interlocked.Add(ref _utc, interval.Ticks);
        while (true)
        {
            TestTimer[] due;
            lock (_sync)
            {
                due = Timers.Where(timer => !timer.Disposed && timer.Due is { } at && at <= _timestamp).ToArray();
                foreach (var timer in due) timer.Due = null;
            }
            if (due.Length == 0) return;
            foreach (var timer in due) timer.FireQueuedCallback();
        }
    }
    internal sealed class TestTimer(CollectionTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal long? Due;
        internal bool Disposed;
        internal bool IsArmed { get { lock (owner._sync) return Due.HasValue && !Disposed; } }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (Disposed) throw new ObjectDisposedException(nameof(TestTimer));
                if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("expected one-shot timer");
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetTimestamp() + dueTime.Ticks;
                return true;
            }
        }
        internal void FireQueuedCallback() => callback(state);
        public void Dispose() { lock (owner._sync) { Disposed = true; Due = null; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
