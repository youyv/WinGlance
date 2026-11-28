using System.Collections;
using Microsoft.Data.Sqlite;
using WinGlance.Core;
using WinGlance.Statistics;

internal static class StatisticsRecordingProgressTests
{
    internal static IEnumerable<(string Name, Func<string, Task> Run)> Cases =>
    [
        ("Accounting age is monotonic, independent of query cache and flush", MonotonicAndCache),
        ("Only completed valid intervals renew recording, repeated baselines cannot renew warmup", EffectiveIntervals),
        ("Queued samples and queued/cancelled resets do not publish progress", QueueAdmission),
        ("Failed reset preserves progress and successful reset waits for a new interval", ResetBoundaries),
        ("Cold startup does not convert persisted UTC to runtime progress", RestartHistory)
    ];

    private static readonly DateTimeOffset Origin = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static MonitorSnapshot Frame(Guid run, long sequence, DateTimeOffset? endUtc = null)
    {
        var end = endUtc ?? Origin;
        return new(end, 1, new(10, 1000, 500, 0, 0, 0, 0, 0, "test"), [], new Dictionary<string, Capability>(), false)
        {
            Accounting = new(run, sequence, end.AddSeconds(-1), end, 1, [],
                new(new Dictionary<StatisticMetric, MetricAccounting> { [StatisticMetric.Cpu] = new(10, 1, 10) }))
        };
    }
    private static MonitorSnapshot Baseline(Guid run, long sequence) => Frame(run, sequence) with
    { Accounting = Frame(run, sequence).Accounting! with { Discontinuity = true, DiscontinuityReason = "首次采样：建立基线" } };
    private static async Task Drain(StatisticsService service) => await service.QueryAsync(StatisticsRange.Today);

    private static async Task MonotonicAndCache(string path)
    {
        var clock = new Clock();
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var cached = await service.QueryAsync(StatisticsRange.Today);
        Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.WaitingForFirstInterval,
            LastRecordedUtc: null, IsStalled: false }, "Cold startup must wait without claiming accounting.");
        clock.Advance(5.999);
        Check(!service.ReadRecordingProgress().IsStalled, "Warmup expired before six seconds.");
        clock.Advance(.001);
        Check(!service.ReadRecordingProgress().IsStalled, "Exactly six seconds should still be warmup.");
        clock.Advance(.001);
        Check(service.ReadRecordingProgress().IsStalled, "Missing first interval was not diagnosed.");
        Check(ReferenceEquals(cached, await service.QueryAsync(StatisticsRange.Today)), "Fixture must hit query cache.");
        var age = service.ReadRecordingProgress().Age;
        clock.Utc = Origin.AddHours(2);
        Check(service.ReadRecordingProgress().Age == age, "UTC forward jump changed runtime age.");
        clock.Utc = Origin.AddHours(-2);
        Check(service.ReadRecordingProgress().Age == age, "UTC backward jump changed runtime age.");
        clock.Utc = Origin;
        service.Accept(Frame(Guid.NewGuid(), 1)); await Drain(service);
        Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.Recording, Age.Ticks: 0,
            IsStalled: false } && service.ReadRecordingProgress().LastRecordedUtc == Origin,
            "Completed effective interval did not publish business time and reset age.");
        var recorded = await service.QueryAsync(StatisticsRange.Today);
        clock.Advance(7);
        Check(ReferenceEquals(recorded, await service.QueryAsync(StatisticsRange.Today)) && service.ReadRecordingProgress().IsStalled,
            "Cache froze runtime progress.");
        await service.FlushAsync();
        Check(service.ReadRecordingProgress().Age == TimeSpan.FromSeconds(7), "Flush falsely renewed accounting.");
    }

    private static async Task EffectiveIntervals(string path)
    {
        var clock = new Clock(); var run = Guid.NewGuid();
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        service.Accept(Frame(run, 1)); await Drain(service);
        clock.Advance(3);
        service.Accept(Frame(run, 1)); service.Accept(Frame(run, 0)); service.Accept(Frame(run, -1));
        var invalid = Frame(run, 2);
        service.Accept(invalid with { Accounting = invalid.Accounting! with { IntervalSeconds = double.NaN } });
        invalid = Frame(run, 3);
        service.Accept(invalid with { Accounting = invalid.Accounting! with { EndUtc = invalid.Accounting!.StartUtc } });
        invalid = Frame(run, 4);
        service.Accept(invalid with { Accounting = invalid.Accounting! with { Processes = new ThrowingProcesses() } });
        await Drain(service);
        Check(service.ReadRecordingProgress().Age == TimeSpan.FromSeconds(3) && service.ReadRecordingProgress().LastRecordedUtc == Origin,
            "Duplicate, reversed, invalid or failed frame renewed recording.");
        service.Accept(Baseline(run, 5)); await Drain(service);
        Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.WaitingForBaseline, Age.Ticks: 0 } &&
            service.ReadRecordingProgress().LastRecordedUtc == Origin, "Baseline must warm up without claiming a recorded interval.");
        clock.Advance(7);
        service.Accept(Baseline(run, 6)); service.Accept(Baseline(run, 6)); await Drain(service);
        Check(service.ReadRecordingProgress().IsStalled && service.ReadRecordingProgress().Age == TimeSpan.FromSeconds(7),
            "Repeated discontinuities refreshed the warmup origin.");
        var nextRun = Guid.NewGuid();
        service.Accept(Baseline(nextRun, 1)); await Drain(service);
        Check(service.ReadRecordingProgress().Age == TimeSpan.Zero && service.ReadRecordingProgress().LastRecordedUtc == Origin,
            "A new collector run should receive one new baseline window while retaining real accounting time.");
        clock.Advance(7);
        service.Accept(Baseline(nextRun, 2)); service.Accept(Baseline(run, 7)); await Drain(service);
        Check(service.ReadRecordingProgress().IsStalled, "Alternating known runs must not renew baseline indefinitely.");
        service.Accept(Frame(nextRun, 3, Origin.AddSeconds(1))); await Drain(service);
        Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.Recording, Age.Ticks: 0 } &&
            service.ReadRecordingProgress().LastRecordedUtc == Origin.AddSeconds(1), "Effective recovery did not clear baseline wait.");
    }

    private static async Task QueueAdmission(string path)
    {
        var clock = new Clock(); var run = Guid.NewGuid();
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        service.Accept(Frame(run, 1)); await Drain(service);
        clock.BlockNext = true;
        var blocked = service.QueryAsync(StatisticsRange.Today);
        await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task reset;
        try
        {
            clock.Advance(9);
            Check(service.Accept(Frame(run, 2, Origin.AddSeconds(1))), "Queued sample was not accepted.");
            reset = service.ResetAsync(StatisticsRange.Today);
            Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.Recording, IsStalled: true } &&
                service.ReadRecordingProgress().LastRecordedUtc == Origin, "Admission or queued reset falsely published worker progress.");
        }
        finally { clock.Release.Set(); }
        await blocked; await reset;
        Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.WaitingAfterReset, LastRecordedUtc: null, Age.Ticks: 0 },
            "Completed reset did not publish waiting state.");
        clock.Advance(7);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await service.ResetAsync(StatisticsRange.Today, cancellation.Token); throw new InvalidOperationException("Cancelled reset ran."); }
        catch (OperationCanceledException) { }
        Check(service.ReadRecordingProgress().IsStalled, "Cancelled reset renewed warmup.");
    }

    private static async Task ResetBoundaries(string path)
    {
        var clock = new Clock(); var run = Guid.NewGuid();
        await using var service = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        service.Accept(Frame(run, 1)); await service.FlushAsync();
        clock.Advance(7);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_recording_reset BEFORE DELETE ON buckets BEGIN SELECT RAISE(ABORT, 'fixture reset failure'); END";
        command.ExecuteNonQuery();
        try
        {
            try { await service.ResetAsync(StatisticsRange.Today); throw new InvalidOperationException("Reset should have failed."); }
            catch (SqliteException) { }
            Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.Recording, IsStalled: true } &&
                service.ReadRecordingProgress().LastRecordedUtc == Origin, "Failed transaction cleared real recording progress.");
        }
        finally { command.CommandText = "DROP TRIGGER fail_recording_reset"; command.ExecuteNonQuery(); }
        clock.Utc = Origin.AddSeconds(20);
        await service.ResetAsync(StatisticsRange.Today);
        clock.Advance(7);
        service.Accept(Frame(run, 2, Origin.AddSeconds(20))); await Drain(service);
        Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.WaitingAfterReset, LastRecordedUtc: null, IsStalled: true },
            "An interval crossing the reset cutoff must not renew progress.");
        clock.Utc = Origin.AddSeconds(22);
        service.Accept(Frame(run, 3, clock.Utc)); await Drain(service);
        Check(service.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.Recording, Age.Ticks: 0 },
            "First effective post-reset interval was not recorded.");
    }

    private static async Task RestartHistory(string path)
    {
        var clock = new Clock();
        await using (var first = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock))
        { first.Accept(Frame(Guid.NewGuid(), 1)); await first.FlushAsync(); }
        clock.Utc = Origin.AddHours(1); clock.Advance(3600);
        await using var restarted = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var historical = await restarted.QueryAsync(StatisticsRange.Today);
        Check(historical.LastRecordedUtc == Origin, "Fixture did not load persisted recording UTC.");
        Check(restarted.ReadRecordingProgress() is { Phase: StatisticsRecordingPhase.WaitingForFirstInterval,
            LastRecordedUtc: null, Age.Ticks: 0, IsStalled: false }, "Persisted UTC became runtime progress after restart.");
    }

    private sealed class ThrowingProcesses : IReadOnlyList<ProcessAccounting>
    {
        public int Count => 1;
        public ProcessAccounting this[int index] => throw new InvalidOperationException("fixture sample failure");
        public IEnumerator<ProcessAccounting> GetEnumerator() => throw new InvalidOperationException("fixture sample failure");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Clock : TimeProvider
    {
        private long _timestamp, _utc = Origin.UtcTicks;
        internal bool BlockNext;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release = new();
        internal DateTimeOffset Utc { get => new(Interlocked.Read(ref _utc), TimeSpan.Zero); set => Interlocked.Exchange(ref _utc, value.UtcTicks); }
        internal void Advance(double seconds) => Interlocked.Add(ref _timestamp, TimeSpan.FromSeconds(seconds).Ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow()
        {
            if (BlockNext)
            {
                BlockNext = false; Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test clock gate");
            }
            return Utc;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new PassiveTimer();
        private sealed class PassiveTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
