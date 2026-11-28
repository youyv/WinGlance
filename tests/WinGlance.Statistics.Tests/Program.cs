using Microsoft.Data.Sqlite;
using WinGlance.Core;
using WinGlance.Statistics;

if (args.Contains("--benchmark")) return await RunBenchmark(args);

var root = Path.Combine(Path.GetTempPath(), "WinGlance.Statistics.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var tests = new List<(string Name, Func<string, Task> Run)>
{
    ("Diagnostic flush reasons, phases, failures and query queue timing", DiagnosticsRegression.Run),
    ("Empty database and unavailable values", async path =>
    {
        var clock = new TestClock(At(25, 12));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var snapshot = await store.QueryAsync(StatisticsRange.Today);
        Equal(0, snapshot.Applications.Count); Equal(0, snapshot.SystemTrend.Count);
        Check(snapshot.System.Metrics[StatisticMetric.Cpu].Average is null, "Empty CPU must be unknown");
    }),
    ("Weighted means, integer bytes, peaks and detached query snapshots", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        clock.Set(At(25, 12).AddSeconds(1));
        Check(store.Accept(Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 101))), "accept");
        var first = await store.QueryAsync(StatisticsRange.CurrentSession);
        clock.Set(clock.GetUtcNow().AddSeconds(3));
        store.Accept(Frame(run, 2, clock.GetUtcNow(), 3, Metrics(90, 3, 205, peak: 70)));
        var result = await store.QueryAsync(StatisticsRange.CurrentSession);
        Near(25, result.System.Metrics[StatisticMetric.Cpu].Average!.Value);
        Near(70, result.System.Metrics[StatisticMetric.Cpu].Peak!.Value);
        Equal(306L, result.Applications.Single().Metrics[StatisticMetric.Download].Bytes!.Value);
        Near(4, result.Applications.Single().ObservedSeconds);
        Near(10, first.System.Metrics[StatisticMetric.Cpu].Average!.Value);
    }),
    ("Duplicate sequences and collector restart", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var frame = Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100));
        store.Accept(frame); store.Accept(frame); await store.FlushAsync(); store.Accept(frame);
        var once = await store.QueryAsync(StatisticsRange.Today);
        Equal(100L, once.System.Metrics[StatisticMetric.Download].Bytes!.Value);
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        store.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(20, 1, 200)));
        var twice = await store.QueryAsync(StatisticsRange.Today);
        Equal(300L, twice.System.Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Midnight integer conservation and natural-day ranges", async path =>
    {
        var clock = new TestClock(At(25, 0).AddSeconds(-1));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        clock.Set(At(25, 0).AddSeconds(1));
        store.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 2, Metrics(20, 2, 101)));
        var current = await store.QueryAsync(StatisticsRange.CurrentSession);
        var today = await store.QueryAsync(StatisticsRange.Today);
        var week = await store.QueryAsync(StatisticsRange.LastSevenDays);
        Equal(101L, current.System.Metrics[StatisticMetric.Download].Bytes!.Value);
        Equal(51L, today.System.Metrics[StatisticMetric.Download].Bytes!.Value);
        Equal(101L, week.System.Metrics[StatisticMetric.Download].Bytes!.Value);
        Near(1, today.System.ObservedSeconds);
    }),
    ("Metric windows are independent from frame duration", async path =>
    {
        var clock = new TestClock(At(25, 0).AddSeconds(1));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var values = Metrics(80, 4, 103);
        values[StatisticMetric.Cpu] = values[StatisticMetric.Cpu] with
        { StartUtc = At(25, 0).AddSeconds(-3), EndUtc = clock.GetUtcNow(), IntervalSeconds = 4 };
        store.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, values));
        var today = await store.QueryAsync(StatisticsRange.Today);
        Near(20, today.System.Metrics[StatisticMetric.Cpu].Average!.Value);
        Near(1, today.System.Metrics[StatisticMetric.Cpu].ValidSeconds);
        Near(20, today.Applications.Single().Metrics[StatisticMetric.Cpu].Average!.Value);
    }),
    ("Unknown samples remain gaps; valid zero is retained", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(run, 1, clock.GetUtcNow(), 1, new()
        { [StatisticMetric.Cpu] = new(null, 0, State: MetricState.PermissionRequired) }));
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        store.Accept(Frame(run, 2, clock.GetUtcNow(), 1, Metrics(0, 1, 0)));
        var result = await store.QueryAsync(StatisticsRange.Today);
        Near(0, result.System.Metrics[StatisticMetric.Cpu].Average!.Value);
        Near(.5, result.System.Metrics[StatisticMetric.Cpu].Coverage);
        Check(result.System.Metrics[StatisticMetric.Gpu].Average is null, "GPU must stay unknown");
    }),
    ("Stable identity across OS sessions and child process union", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var one = Software("file:1:C:\\Apps\\Browser.exe");
        var two = one with { Key = "file:8:C:\\Apps\\Browser.exe" };
        var sample = Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100), software: one);
        var original = sample.Accounting!.Processes[0];
        sample = sample with { Accounting = sample.Accounting with { Processes = new[] { original,
            new ProcessAccounting(new(99, 2000), two, 1, Metrics(20, 1, 200)) } } };
        store.Accept(sample);
        var result = await store.QueryAsync(StatisticsRange.Today);
        Equal(1, result.Applications.Count); Near(1, result.Applications.Single().ObservedSeconds);
        Near(30, result.Applications.Single().Metrics[StatisticMetric.Cpu].Average!.Value);
        Equal(300L, result.Applications.Single().Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Identical descriptions at different paths remain distinct", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 10), software: Software("file:1:C:\\A.exe", "C:\\A.exe")));
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        store.Accept(Frame(run, 2, clock.GetUtcNow(), 1, Metrics(10, 1, 10), software: Software("file:1:C:\\B.exe", "C:\\B.exe")));
        Equal(2, (await store.QueryAsync(StatisticsRange.Today)).Applications.Count);
    }),
    ("Physical GPU engine aggregation", async path =>
    {
        var clock = new TestClock(At(25, 12));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var sample = Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(1, 1, 1));
        var metrics = Metrics(1, 1, 1); metrics[StatisticMetric.Gpu] = new(30, 1, 30);
        var software = Software("file:1:C:\\Apps\\Browser.exe");
        sample = sample with { Accounting = sample.Accounting! with { Processes = new[] {
            new ProcessAccounting(new(1,100),software,1,metrics,new Dictionary<string,MetricAccounting>{{"gpu0:engine0",new(30,1,30)}}),
            new ProcessAccounting(new(2,200),software,1,metrics,new Dictionary<string,MetricAccounting>{{"gpu0:engine1",new(30,1,30)}}) } } };
        store.Accept(sample);
        Near(30, (await store.QueryAsync(StatisticsRange.Today)).Applications.Single().Metrics[StatisticMetric.Gpu].Average!.Value);
    }),
    ("Exited application and retained icon", async path =>
    {
        var clock = new TestClock(At(25, 12));
        string icon = Path.Combine(Path.GetDirectoryName(path)!, "source.png");
        File.WriteAllBytes(icon, [1,2,3]);
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var software = Software("file:1:C:\\Apps\\Browser.exe") with { IconPath = icon };
        store.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100), software: software, running: false));
        var app = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single();
        Check(!app.IsRunning, "Exited app must remain historical");
        File.Delete(icon); Check(File.Exists(app.Software.IconPath), "Historical icon should be copied");
    }),
    ("Persistence, sequence watermark and session identity", async path =>
    {
        var clock = new TestClock(At(25, 12)); var session = Guid.NewGuid(); var run = Guid.NewGuid();
        var frame = Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100));
        await using (var store = new StatisticsService(path, TimeZoneInfo.Utc, session, clock)) store.Accept(frame);
        await using var reopened = new StatisticsService(path, TimeZoneInfo.Utc, session, clock);
        reopened.Accept(frame);
        var result = await reopened.QueryAsync(StatisticsRange.CurrentSession);
        Equal(100L, result.System.Metrics[StatisticMetric.Download].Bytes!.Value);
        Check(result.LastSavedUtc is not null, "Persisted save timestamp");
    }),
    ("Current-session reset preserves other sessions; today reset crosses sessions", async path =>
    {
        var clock = new TestClock(At(25, 12));
        await using (var first = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock))
            first.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100)));
        clock.Set(clock.GetUtcNow().AddSeconds(2));
        await using var second = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        second.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(20, 1, 200)));
        Equal(300L, (await second.QueryAsync(StatisticsRange.Today)).System.Metrics[StatisticMetric.Download].Bytes!.Value);
        await second.ResetAsync(StatisticsRange.CurrentSession);
        Equal(100L, (await second.QueryAsync(StatisticsRange.Today)).System.Metrics[StatisticMetric.Download].Bytes!.Value);
        Equal(0, (await second.QueryAsync(StatisticsRange.CurrentSession)).Applications.Count);
        await second.ResetAsync(StatisticsRange.Today);
        Equal(0, (await second.QueryAsync(StatisticsRange.Today)).Applications.Count);
    }),
    ("Reset barrier rejects old frames and resumes new intervals", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100)));
        await store.ResetAsync(StatisticsRange.Today);
        store.Accept(Frame(run, 2, clock.GetUtcNow(), 1, Metrics(10, 1, 100)));
        Equal(0, (await store.QueryAsync(StatisticsRange.Today)).Applications.Count);
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        store.Accept(Frame(run, 3, clock.GetUtcNow(), 1, Metrics(10, 1, 33)));
        Equal(33L, (await store.QueryAsync(StatisticsRange.Today)).System.Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Long current session retains totals beyond seven days", async path =>
    {
        var clock = new TestClock(At(15, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100))); await store.FlushAsync();
        clock.Set(At(25, 12)); store.Accept(Frame(run, 2, clock.GetUtcNow(), 1, Metrics(10, 1, 200))); await store.FlushAsync();
        Equal(300L, (await store.QueryAsync(StatisticsRange.CurrentSession)).System.Metrics[StatisticMetric.Download].Bytes!.Value);
        Equal(200L, (await store.QueryAsync(StatisticsRange.LastSevenDays)).System.Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Only application five-minute buckets roll up, idempotently", async path =>
    {
        var clock = new TestClock(At(23, 10));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100)));
        clock.Set(At(25, 12)); await store.FlushAsync(); await store.FlushAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM buckets WHERE entity='@system' AND kind=60";
        Equal(1L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT COUNT(*) FROM buckets WHERE entity<>'@system' AND kind=3600";
        Equal(1L, (long)command.ExecuteScalar()!);
        Equal(100L, (await store.QueryAsync(StatisticsRange.LastSevenDays)).System.Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Changing capacities suppress misleading historical percentages", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 100), capacity: 1000));
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        store.Accept(Frame(run, 2, clock.GetUtcNow(), 1, Metrics(10, 1, 100), capacity: 2000));
        Check((await store.QueryAsync(StatisticsRange.Today)).TotalMemoryBytes is null, "Changed capacity must not be reused for history");
    }),
    ("Disjoint child lifetimes use a time union and simultaneous peaks", async path =>
    {
        var clock = new TestClock(At(25, 12));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var end = clock.GetUtcNow(); var middle = end.AddSeconds(-.5); var start = end.AddSeconds(-1);
        var sample = Frame(Guid.NewGuid(), 1, end, 1, Metrics(1, 1, 1));
        var software = Software("file:1:C:\\Apps\\Browser.exe");
        var early = WindowMetrics(20, .5, 101, start, middle);
        var late = WindowMetrics(40, .5, 202, middle, end);
        sample = sample with { Accounting = sample.Accounting! with { Processes = new[] {
            new ProcessAccounting(new(1,100), software, .5, early, ObservedStartUtc: start, ObservedEndUtc: middle),
            new ProcessAccounting(new(2,200), software, .5, late, ObservedStartUtc: middle, ObservedEndUtc: end) } } };
        store.Accept(sample);
        var app = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single();
        Near(1, app.ObservedSeconds); Near(60, app.Metrics[StatisticMetric.Cpu].Average!.Value);
        Near(80, app.Metrics[StatisticMetric.Cpu].Peak!.Value); Near(1, app.Metrics[StatisticMetric.Cpu].Coverage);
        Equal(303L, app.Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Missing child preserves measured integer totals without inventing a complete average", async path =>
    {
        var clock = new TestClock(At(25, 12));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var sample = Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(10, 1, 101));
        var one = sample.Accounting!.Processes[0];
        sample = sample with { Accounting = sample.Accounting with { Processes = new[] { one,
            new ProcessAccounting(new(99,2000), one.Software, 1, new Dictionary<StatisticMetric,MetricAccounting>()) } } };
        store.Accept(sample);
        var app = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single();
        Equal(101L, app.Metrics[StatisticMetric.Download].Bytes!.Value);
        Equal(10000L, app.Metrics[StatisticMetric.Cpu].CpuTicks!.Value);
        Check(app.Metrics[StatisticMetric.Cpu].Average is null, "Incomplete CPU average stays unknown");
        Near(0, app.Metrics[StatisticMetric.Cpu].Coverage);
    }),
    ("GPU windows survive app aggregation across midnight", async path =>
    {
        var clock = new TestClock(At(25, 0).AddSeconds(1));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var sample = Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 1, Metrics(1, 1, 1));
        var software = Software("file:1:C:\\Apps\\Browser.exe");
        var gpu = new MetricAccounting(120, 4, 30, IntervalSeconds: 4,
            StartUtc: clock.GetUtcNow().AddSeconds(-4), EndUtc: clock.GetUtcNow());
        var metrics = new Dictionary<StatisticMetric,MetricAccounting> { [StatisticMetric.Gpu] = gpu };
        sample = sample with { Accounting = sample.Accounting! with { Processes = new[] {
            new ProcessAccounting(new(1,100), software, 1, metrics, new Dictionary<string,MetricAccounting>{{"gpu0:engine0",gpu}}),
            new ProcessAccounting(new(2,200), software, 1, metrics, new Dictionary<string,MetricAccounting>{{"gpu0:engine1",gpu}}) } } };
        store.Accept(sample);
        var today = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single().Metrics[StatisticMetric.Gpu];
        var session = (await store.QueryAsync(StatisticsRange.CurrentSession)).Applications.Single().Metrics[StatisticMetric.Gpu];
        Near(1, today.ValidSeconds); Near(30, today.Integral!.Value); Near(120, session.Integral!.Value);
    }),
    ("Large integer byte counts survive midpoint splitting exactly", async path =>
    {
        const long bytes = 9007199254740993;
        var clock = new TestClock(At(25, 0).AddSeconds(1));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 2, Metrics(20, 2, bytes)));
        var session = await store.QueryAsync(StatisticsRange.CurrentSession);
        var today = await store.QueryAsync(StatisticsRange.Today);
        Equal(bytes, session.Applications.Single().Metrics[StatisticMetric.Download].Bytes!.Value);
        Equal(4503599627370497L, today.Applications.Single().Metrics[StatisticMetric.Download].Bytes!.Value);
        Equal(bytes, session.System.Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Observed lifetimes split into the correct natural day", async path =>
    {
        var clock = new TestClock(At(25, 0).AddSeconds(1));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var sample = Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 2, Metrics(10, 2, 10));
        var member = sample.Accounting!.Processes[0] with { ObservedSeconds = .5,
            ObservedStartUtc = At(25, 0).AddSeconds(-1), ObservedEndUtc = At(25, 0).AddSeconds(-.5),
            Metrics = WindowMetrics(10, .5, 10, At(25, 0).AddSeconds(-1), At(25, 0).AddSeconds(-.5)) };
        store.Accept(sample with { Accounting = sample.Accounting with { Processes = new[] { member } } });
        Near(.5, (await store.QueryAsync(StatisticsRange.CurrentSession)).Applications.Single().ObservedSeconds);
        Equal(0, (await store.QueryAsync(StatisticsRange.Today)).Applications.Count);
    }),
    ("Asynchronous CPU windows keep uncovered child tails unknown across frames", async path =>
    {
        var clock = new TestClock(At(25, 12).AddSeconds(1.1)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var origin = At(25, 12); var software = Software("file:1:C:\\Apps\\Browser.exe");
        for (int index = 0; index < 2; index++)
        {
            var end = origin.AddSeconds(index + 1.1); clock.Set(end);
            var a = WindowMetrics(10, 1, 100, origin.AddSeconds(index), origin.AddSeconds(index + 1));
            var b = WindowMetrics(20, 1, 200, origin.AddSeconds(index + .02), origin.AddSeconds(index + 1.02));
            var sample = Frame(run, index + 1, end, 1, Metrics(30, 1, 300));
            store.Accept(sample with { Accounting = sample.Accounting! with { Processes = new[] {
                new ProcessAccounting(new(1,100), software, 1, a, ObservedStartUtc: end.AddSeconds(-1), ObservedEndUtc: end),
                new ProcessAccounting(new(2,200), software, 1, b, ObservedStartUtc: end.AddSeconds(-1), ObservedEndUtc: end) } } });
        }
        var cpu = (await store.QueryAsync(StatisticsRange.Today)).Applications.Single().Metrics[StatisticMetric.Cpu];
        Near(1.96, cpu.ValidSeconds); Near(30, cpu.Average!.Value); Near(30, cpu.Peak!.Value);
        Equal(60000L, cpu.CpuTicks!.Value);
    }),
    ("Initial baseline is quiet and genuine gaps retain a timestamp", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var sample = Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 10));
        store.Accept(sample with { Accounting = sample.Accounting! with { Discontinuity = true, DiscontinuityReason = "首次采样，正在建立统计基线" } });
        Check((await store.QueryAsync(StatisticsRange.Today)).Warning is null, "Expected startup is not a lasting warning");
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        sample = Frame(run, 2, clock.GetUtcNow(), 1, Metrics(10, 1, 10));
        store.Accept(sample with { Accounting = sample.Accounting! with { Discontinuity = true, DiscontinuityReason = "sleep" } });
        await store.FlushAsync();
        var warning = (await store.QueryAsync(StatisticsRange.Today)).Warning;
        Check(warning?.Contains("11-25 12:00:01") == true && warning.Contains("sleep"), "Real gap remains dated after successful flush");
    }),
    ("Successful retry clears save error while preserving real gaps", async path =>
    {
        var clock = new TestClock(At(25, 12)); var run = Guid.NewGuid();
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var gap = Frame(run, 1, clock.GetUtcNow(), 1, Metrics(10, 1, 10));
        store.Accept(gap with { Accounting = gap.Accounting! with { Discontinuity = true, DiscontinuityReason = "sleep" } });
        await store.FlushAsync();
        await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await blocker.OpenAsync();
        using var transaction = blocker.BeginTransaction();
        clock.Set(clock.GetUtcNow().AddSeconds(1)); store.Accept(Frame(run, 2, clock.GetUtcNow(), 1, Metrics(10, 1, 10)));
        bool failed = false;
        try { await store.FlushAsync(); } catch (SqliteException) { failed = true; }
        Check(failed, "Write lock should cause a bounded save failure");
        Check((await store.QueryAsync(StatisticsRange.Today)).Warning?.Contains("暂未完整保存") == true, "Save failure is visible");
        transaction.Rollback(); await store.FlushAsync();
        var result = await store.QueryAsync(StatisticsRange.Today);
        Check(result.Warning?.Contains("sleep") == true && !result.Warning.Contains("暂未完整保存"), "Retry clears only the save failure");
        Equal(10L, result.System.Metrics[StatisticMetric.Download].Bytes!.Value);
    }),
    ("Discontinuities do not inflate time or bytes", async path =>
    {
        var clock = new TestClock(At(25, 12));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        var sample = Frame(Guid.NewGuid(), 1, clock.GetUtcNow(), 3600, Metrics(36000, 3600, 100));
        store.Accept(sample with { Accounting = sample.Accounting! with { Discontinuity = true, DiscontinuityReason = "sleep" } });
        var result = await store.QueryAsync(StatisticsRange.Today);
        Near(0, result.System.ObservedSeconds); Check(result.Warning is not null, "Discontinuity warning");
    })
};

tests.AddRange(CatalogCleanupTests.Cases);
tests.AddRange(OptimizationRegressionTests.Cases);
tests.AddRange(StatisticsExportTests.Cases);
int failed = 0;
foreach (var (name, run) in tests)
{
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try { await run(Path.Combine(directory, "statistics.db")); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} statistics tests passed.");
// Every test creates its own database; no application data or running application is touched.
if (failed == 0 && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
    Directory.Delete(root, recursive: true);
else Console.WriteLine("Test data retained: " + root);
return failed == 0 ? 0 : 1;

static DateTimeOffset At(int day, int hour) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) where T : IEquatable<T>
{ if (!expected.Equals(actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void Near(double expected, double actual)
{ if (Math.Abs(expected - actual) > 0.00001) throw new Exception($"Expected {expected}, got {actual}"); }
static SoftwareIdentity Software(string key, string path = "C:\\Apps\\Browser.exe") =>
    new(key, "Browser", "Browser.exe", path, "Example", SoftwareCategory.Application, "synthetic");
static Dictionary<StatisticMetric, MetricAccounting> Metrics(double cpuIntegral, double seconds, long bytes, double? peak = null) => new()
{
    [StatisticMetric.Cpu] = new(cpuIntegral, seconds, peak ?? cpuIntegral / seconds, CpuTicksDelta: (long)(cpuIntegral * 1000), IntervalSeconds: seconds),
    [StatisticMetric.Download] = new(bytes, seconds, bytes / seconds, BytesDelta: bytes, IntervalSeconds: seconds),
    [StatisticMetric.Memory] = new(1000 * seconds, seconds, 1000, IntervalSeconds: seconds)
};
static Dictionary<StatisticMetric, MetricAccounting> WindowMetrics(double cpuIntegral, double seconds, long bytes,
    DateTimeOffset start, DateTimeOffset end) => Metrics(cpuIntegral, seconds, bytes)
        .ToDictionary(entry => entry.Key, entry => entry.Value with { StartUtc = start, EndUtc = end });
static MonitorSnapshot Frame(Guid run, long sequence, DateTimeOffset end, double seconds,
    Dictionary<StatisticMetric, MetricAccounting> metrics, SoftwareIdentity? software = null, bool running = true, long capacity = 1000)
{
    software ??= Software("file:1:C:\\Apps\\Browser.exe"); var identity = new ProcessIdentity(42, 1000);
    var process = new ProcessSample(identity, software, 0, 1000, 0, 0, 0, 0, new Dictionary<string,double>(), 0, 0);
    return new(end, seconds, new(0, capacity, 0, 0, 0, 0, 0, 0, "test"), running ? new[] { process } : [],
        new Dictionary<string,Capability>(), false)
    { Accounting = new(run, sequence, end.AddSeconds(-seconds), end, seconds,
        new[] { new ProcessAccounting(identity, software, seconds, metrics) },
        new(metrics, NetworkScope: "test", TotalMemoryBytes: capacity, DedicatedGpuCapacityBytes: 500)) };
}

static async Task<int> RunBenchmark(string[] arguments)
{
    int index = Array.IndexOf(arguments, "--output");
    string directory = index >= 0 && index + 1 < arguments.Length ? Path.GetFullPath(arguments[index + 1]) :
        Path.Combine(Path.GetTempPath(), "WinGlance.Statistics.Benchmark-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "benchmark-" + Guid.NewGuid().ToString("N") + ".db");
    int Option(string name, int fallback)
    { int found = Array.IndexOf(arguments, name); return found >= 0 && found + 1 < arguments.Length ? int.Parse(arguments[found + 1]) : fallback; }
    int appCount = Option("--apps", 300), processesPerApp = Option("--children", 2), samples = Option("--samples", 65);
    var run = Guid.NewGuid(); var origin = DateTimeOffset.UtcNow; var records = new List<object>();
    var queryTimes = new List<double>(); var acceptTimes = new List<double>(); var flushTimes = new List<double>();
    var current = System.Diagnostics.Process.GetCurrentProcess();
    void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); current.Refresh(); }
    Collect(); long baselinePrivate = current.PrivateMemorySize64, baselineManaged = GC.GetTotalMemory(false);
    long allocatedBefore = GC.GetTotalAllocatedBytes(true); var cpuBefore = current.TotalProcessorTime;
    TimeSpan? warmCpu = null; double warmWall = 0;
    var wall = System.Diagnostics.Stopwatch.StartNew();
    await using (var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: new ManualBenchmarkClock()))
    {
        for (int sequence = 1; sequence <= samples; sequence++)
        {
            var end = origin.AddSeconds(sequence); var start = end.AddSeconds(-1);
            var processes = new List<ProcessAccounting>(appCount * processesPerApp);
            var live = new List<ProcessSample>(appCount * processesPerApp);
            for (int app = 0; app < appCount; app++)
            {
                var software = new SoftwareIdentity($"file:1:C:\\Synthetic\\App{app}.exe", $"Application {app}",
                    $"App{app}.exe", $"C:\\Synthetic\\App{app}.exe", "Synthetic", SoftwareCategory.Application, "benchmark");
                for (int child = 0; child < processesPerApp; child++)
                {
                    var identity = new ProcessIdentity(app * processesPerApp + child + 100, origin.AddMinutes(-1).UtcTicks);
                    var metrics = WindowMetrics(.05, 1, 1024 + app, start, end);
                    metrics[StatisticMetric.Cpu] = metrics[StatisticMetric.Cpu] with { CpuTicksDelta = 80000 };
                    metrics[StatisticMetric.Gpu] = new(.02, 1, .02, IntervalSeconds: 1, StartUtc: start, EndUtc: end);
                    metrics[StatisticMetric.DedicatedGpuMemory] = new(1048576, 1, 1048576, IntervalSeconds: 1, StartUtc: start, EndUtc: end);
                    metrics[StatisticMetric.SharedGpuMemory] = new(524288, 1, 524288, IntervalSeconds: 1, StartUtc: start, EndUtc: end);
                    metrics[StatisticMetric.Upload] = new(128, 1, 128, BytesDelta: 128, IntervalSeconds: 1, StartUtc: start, EndUtc: end);
                    metrics[StatisticMetric.DiskRead] = new(2048, 1, 2048, BytesDelta: 2048, IntervalSeconds: 1, StartUtc: start, EndUtc: end);
                    metrics[StatisticMetric.DiskWrite] = new(512, 1, 512, BytesDelta: 512, IntervalSeconds: 1, StartUtc: start, EndUtc: end);
                    processes.Add(new(identity, software, 1, metrics,
                        new Dictionary<string, MetricAccounting> { ["gpu0:engine0"] = metrics[StatisticMetric.Gpu] }, start, end));
                    live.Add(new(identity, software, 0, 1000, 0, 0, 0, 0, new Dictionary<string,double>(), 0, 0));
                }
            }
            var frame = Frame(run, sequence, end, 1, Metrics(30, 1, 900000)) with { Processes = live };
            frame = frame with { Accounting = frame.Accounting! with { Processes = processes } };
            var measured = System.Diagnostics.Stopwatch.StartNew();
            if (!store.Accept(frame)) throw new Exception("Benchmark sample was unexpectedly rejected.");
            acceptTimes.Add(measured.Elapsed.TotalMilliseconds);
            // Query is also a queue barrier; it measures both aggregation latency and materialization cost.
            measured.Restart(); var snapshot = await store.QueryAsync(StatisticsRange.Today);
            double drainAndQuery = measured.Elapsed.TotalMilliseconds;
            measured.Restart(); snapshot = await store.QueryAsync(StatisticsRange.CurrentSession);
            queryTimes.Add(measured.Elapsed.TotalMilliseconds);
            if (snapshot.Applications.Count != appCount) throw new Exception("Benchmark app count mismatch.");
            if (sequence % 30 == 0)
            { measured.Restart(); await store.FlushAsync(); flushTimes.Add(measured.Elapsed.TotalMilliseconds); }
            current.Refresh();
            if (sequence == 15) { warmCpu = current.TotalProcessorTime; warmWall = wall.Elapsed.TotalSeconds; }
            records.Add(new { Sequence = sequence, PrivateMiB = current.PrivateMemorySize64 / 1048576d,
                ManagedMiB = GC.GetTotalMemory(false) / 1048576d, DrainAndQueryMs = drainAndQuery,
                QueryMs = queryTimes[^1], CpuSeconds = (current.TotalProcessorTime - cpuBefore).TotalSeconds, LastSavedUtc = snapshot.LastSavedUtc });
            if (sequence % 15 == 0) Console.WriteLine($"BENCHMARK {sequence}/{samples}: queue+query {drainAndQuery:F1} ms, private {current.PrivateMemorySize64 / 1048576d:F1} MiB");
            var delay = TimeSpan.FromSeconds(sequence) - wall.Elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
        }
        Collect();
        records.Add(new { Sequence = samples + 1, PrivateMiB = current.PrivateMemorySize64 / 1048576d,
            ManagedMiB = GC.GetTotalMemory(false) / 1048576d, Phase = "after forced GC with live service" });
        long steadyPrivate = current.PrivateMemorySize64, steadyManaged = GC.GetTotalMemory(false);
        double cpuSeconds = (current.TotalProcessorTime - cpuBefore).TotalSeconds;
        double P95(List<double> values) => values.Order().ElementAt((int)Math.Floor((values.Count - 1) * .95));
        var report = new { DateUtc = DateTimeOffset.UtcNow, ApplicationCount = appCount, ProcessesPerApp = processesPerApp,
            Samples = samples, DurationSeconds = wall.Elapsed.TotalSeconds, LogicalProcessors = Environment.ProcessorCount,
            CpuSeconds = cpuSeconds, CpuPercentOfMachine = cpuSeconds / wall.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
            SteadyCpuPercentOfMachine = warmCpu is { } warm ?
                (current.TotalProcessorTime - warm).TotalSeconds / (wall.Elapsed.TotalSeconds - warmWall) / Environment.ProcessorCount * 100 : (double?)null,
            BaselinePrivateMiB = baselinePrivate / 1048576d, SteadyPrivateMiB = steadyPrivate / 1048576d,
            PrivateIncreaseMiB = (steadyPrivate - baselinePrivate) / 1048576d,
            ManagedIncreaseMiB = (steadyManaged - baselineManaged) / 1048576d,
            TotalAllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576d,
            AcceptP95Ms = P95(acceptTimes), QueryP95Ms = P95(queryTimes), FlushMilliseconds = flushTimes, SamplesDetail = records };
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(directory, "statistics-benchmark.json"), json);
        Console.WriteLine(json);
    }
    return 0;
}
sealed class TestClock(DateTimeOffset instant) : TimeProvider
{
    private DateTimeOffset _now = instant;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset instant) => _now = instant;
}
// The benchmark explicitly calls the same public flush operation at each 30-second boundary.
// Disable the background timer only in this harness to avoid measuring two back-to-back writes.
sealed class ManualBenchmarkClock : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new ManualTimer();
    private sealed class ManualTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
