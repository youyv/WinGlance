using Microsoft.Data.Sqlite;
using WinGlance.Core;
using WinGlance.Statistics;

internal static class OptimizationRegressionTests
{
    public static IEnumerable<(string Name, Func<string, Task> Run)> Cases =>
    [
        ("Host identity cache separates PID reuse and collector restarts", HostIdentity),
        ("Cached totals and dirty coverage survive a mid-transaction rollback", RollbackCachedTotals),
        ("Unchanged coverage is not rewritten on an empty flush", UnchangedCoverage),
        ("Ready GPU zero contributes time while unavailable GPU stays a gap", ZeroGpu),
    ];

    private static async Task HostIdentity(string path)
    {
        var end = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(end); var run = Guid.NewGuid();
        var software = new SoftwareIdentity("process:42:host", "Host", "host.exe", null, null,
            SoftwareCategory.Application, "test");
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(run, 1, end, new(42, 1000), software));
        clock.Set(end.AddSeconds(1));
        store.Accept(Frame(run, 2, clock.GetUtcNow(), new(42, 1000), software));
        var first = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single();
        Check(first.Metrics[StatisticMetric.Download].Bytes == 200, "A live process instance must keep one identity");
        clock.Set(end.AddSeconds(2));
        store.Accept(Frame(run, 3, clock.GetUtcNow(), new(42, 2000), software));
        clock.Set(end.AddSeconds(3));
        store.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), new(42, 2000), software));
        var result = await store.QueryAsync(StatisticsRange.Today);
        Check(result.Applications.Count == 3, "PID reuse and collector restart must create distinct host identities");
        Check(result.Applications.Single(app => app.AppId == first.AppId).Metrics[StatisticMetric.Download].Bytes == 200,
            "A reused PID must not change the original application's totals");
    }

    private static async Task RollbackCachedTotals(string path)
    {
        var end = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(end); var run = Guid.NewGuid();
        await using (var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock))
        {
            store.Accept(Frame(run, 1, end));
            await store.FlushAsync();
            await store.QueryAsync(StatisticsRange.Today);
            Execute(path, "CREATE TRIGGER block_coverage BEFORE INSERT ON coverage BEGIN SELECT RAISE(ABORT, 'injected failure after bucket writes'); END");
            clock.Set(end.AddSeconds(1)); store.Accept(Frame(run, 2, clock.GetUtcNow()));
            bool failed = false;
            try { await store.FlushAsync(); } catch (SqliteException) { failed = true; }
            Check(failed, "The injected error must fail after merged buckets were written within the transaction");
            var pending = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single();
            Check(pending.Metrics[StatisticMetric.Download].Bytes == 200, "A rolled-back cache must not count pending deltas twice");
            Check(Scalar(path, "SELECT MAX(finish) FROM coverage") == end.UtcTicks, "Rollback must preserve the persisted coverage watermark");
            Execute(path, "DROP TRIGGER block_coverage");
            await store.FlushAsync();
            Check(Scalar(path, "SELECT MAX(finish) FROM coverage") == end.AddSeconds(1).UtcTicks,
                "Dirty coverage must survive failure and persist on retry");
            await store.FlushAsync();
            var detail = await store.QueryApplicationAsync(StatisticsRange.Today, pending.AppId);
            Check(detail.Application!.Metrics[StatisticMetric.Download].Bytes == 200, "Repeated successful flushes must be idempotent");
            Check(detail.Trend.Sum(point => point.Metrics[StatisticMetric.Download].Bytes ?? 0) == 200,
                "Cached trend buckets must match daily totals after rollback and retry");
        }
        await using var reopened = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        Check((await reopened.QueryAsync(StatisticsRange.Today)).Applications.Single().Metrics[StatisticMetric.Download].Bytes == 200,
            "Reopening the database must preserve the same exact totals");
    }

    private static async Task UnchangedCoverage(string path)
    {
        var end = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: new TestClock(end));
        store.Accept(Frame(Guid.NewGuid(), 1, end));
        await store.FlushAsync();
        Execute(path, "CREATE TRIGGER unexpected_coverage BEFORE INSERT ON coverage BEGIN SELECT RAISE(ABORT, 'unchanged watermark rewritten'); END");
        try { await store.FlushAsync(); }
        finally { Execute(path, "DROP TRIGGER unexpected_coverage"); }
        Check((await store.QueryAsync(StatisticsRange.Today)).Applications.Single().Metrics[StatisticMetric.Download].Bytes == 100,
            "An empty flush must keep the stored totals");
    }

    private static async Task ZeroGpu(string path)
    {
        var end = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(end); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        for (int index = 0; index < 3; index++)
        {
            clock.Set(end.AddSeconds(index));
            var gpu = index == 2 ? new MetricAccounting(null, 0, State: MetricState.PermissionRequired) :
                new MetricAccounting(index * 100, 1, index * 100, IntervalSeconds: 1);
            var frame = Frame(run, index + 1, clock.GetUtcNow());
            var process = frame.Accounting!.Processes.Single();
            var metrics = new Dictionary<StatisticMetric, MetricAccounting> { [StatisticMetric.Gpu] = gpu };
            store.Accept(frame with { Accounting = frame.Accounting with { Processes = [process with { Metrics = metrics }], System = new(metrics) } });
            if (index == 0)
            {
                var zero = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single().Metrics[StatisticMetric.Gpu];
                Check(zero.Average == 0 && zero.ValidSeconds == 1 && zero.Peak == 0, "Ready zero must remain an observed zero");
            }
        }
        var result = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single().Metrics[StatisticMetric.Gpu];
        Check(result.Average == 50 && result.ValidSeconds == 2 && Math.Abs(result.Coverage - 2d / 3) < .00001,
            "Zero and active GPU samples must average to 50%, with the unknown interval excluded");
    }

    private static MonitorSnapshot Frame(Guid run, long sequence, DateTimeOffset end, ProcessIdentity? identity = null,
        SoftwareIdentity? software = null)
    {
        software ??= new("file:C:\\Synthetic\\App.exe", "App", "App.exe", "C:\\Synthetic\\App.exe", "Synthetic", SoftwareCategory.Application, "test");
        var instance = identity ?? new ProcessIdentity(42, 1000);
        var metrics = new Dictionary<StatisticMetric, MetricAccounting>
        { [StatisticMetric.Download] = new(100, 1, 100, BytesDelta: 100, IntervalSeconds: 1) };
        var process = new ProcessSample(instance, software, 0, 1000, 0, 0, 0, 0, new Dictionary<string, double>(), 0, 0);
        return new(end, 1, new(0, 1000, 0, 0, 0, 0, 0, 0, "test"), [process], new Dictionary<string, Capability>(), false)
        { Accounting = new(run, sequence, end.AddSeconds(-1), end, 1,
            [new ProcessAccounting(instance, software, 1, metrics)], new(metrics)) };
    }

    private static void Execute(string path, string sql) => Scalar(path, sql);
    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar() is long value ? value : 0;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
