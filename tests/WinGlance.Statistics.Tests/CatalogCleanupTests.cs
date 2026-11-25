using Microsoft.Data.Sqlite;
using WinGlance.Core;
using WinGlance.Statistics;

internal static class CatalogCleanupTests
{
    public static IEnumerable<(string Name, Func<string, Task> Run)> Cases =>
    [
        ("Reset removes unreferenced catalog and owned icon immediately", ResetRemovesUnreferenced),
        ("Session reset retains another session's catalog and shared icon", ResetPreservesOtherSession),
        ("Today reset preserves older dates and removes today's cross-session catalog", ResetPreservesOtherDate),
        ("Reset retains a live icon until its last process exits", ResetPreservesRunning),
        ("Locked orphan icon is retried by the next flush", RetryLockedIcon),
        ("Restart retries a locked orphan while preserving referenced and unrelated files", RestartRetriesOrphan),
    ];

    private static async Task ResetRemovesUnreferenced(string path)
    {
        var clock = new TestClock(At(25));
        var software = Software(path, "removed");
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(software, clock.GetUtcNow()));
        string icon = await Icon(store, "removed");
        Check(File.Exists(icon), "The owned icon must exist before reset");
        await store.ResetAsync(StatisticsRange.CurrentSession);
        Check(CatalogCount(path) == 0, "Reset must remove the unreferenced catalog row immediately");
        Check(!File.Exists(icon), "Reset must remove the owned icon immediately");
        Check(File.Exists(software.IconPath), "The original collector icon is outside statistics ownership");
    }

    private static async Task ResetPreservesOtherSession(string path)
    {
        var clock = new TestClock(At(25));
        var shared = Software(path, "shared");
        string sharedIcon;
        await using (var first = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock))
        {
            first.Accept(Frame(shared, clock.GetUtcNow()));
            sharedIcon = await Icon(first, "shared");
        }
        clock.Set(clock.GetUtcNow().AddSeconds(2));
        await using var second = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        second.Accept(Frame(shared, clock.GetUtcNow()));
        second.Accept(Frame(Software(path, "current"), clock.GetUtcNow()));
        string currentIcon = await Icon(second, "current");
        await second.ResetAsync(StatisticsRange.CurrentSession);
        Check(CatalogCount(path) == 1, "Only the application referenced by another session should remain");
        Check(File.Exists(sharedIcon) && !File.Exists(currentIcon), "Shared metadata must outlive a single-session reset");
        var remaining = (await second.QueryAsync(StatisticsRange.Today)).Applications.Single();
        Check(remaining.Software.DisplayName == "shared" && remaining.Metrics[StatisticMetric.Download].Bytes == 100,
            "The previous session's exact accounting must survive");
    }

    private static async Task ResetPreservesOtherDate(string path)
    {
        var clock = new TestClock(At(24));
        var older = Software(path, "older");
        string olderIcon;
        await using (var first = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock))
        {
            first.Accept(Frame(older, clock.GetUtcNow()));
            olderIcon = await Icon(first, "older");
            clock.Set(At(25));
            first.Accept(Frame(Software(path, "today-first"), clock.GetUtcNow()));
        }
        await using var second = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        second.Accept(Frame(older, clock.GetUtcNow()));
        second.Accept(Frame(Software(path, "today-second"), clock.GetUtcNow()));
        string todayFirst = await Icon(second, "today-first"), todaySecond = await Icon(second, "today-second");
        await second.ResetAsync(StatisticsRange.Today);
        Check(CatalogCount(path) == 1 && File.Exists(olderIcon), "Yesterday's shared catalog and icon must survive");
        Check(!File.Exists(todayFirst) && !File.Exists(todaySecond), "Today reset must clean rows across both sessions");
        Check((await second.QueryAsync(StatisticsRange.Today)).Applications.Count == 0, "Today's range is empty");
        var remaining = (await second.QueryAsync(StatisticsRange.LastSevenDays)).Applications.Single();
        Check(remaining.Software.DisplayName == "older" && remaining.Metrics[StatisticMetric.Download].Bytes == 100,
            "Yesterday's exact accounting must survive");
    }

    private static async Task ResetPreservesRunning(string path)
    {
        var clock = new TestClock(At(25));
        var software = Software(path, "running");
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(software, clock.GetUtcNow(), running: true));
        string icon = await Icon(store, "running");
        await store.ResetAsync(StatisticsRange.CurrentSession);
        Check(CatalogCount(path) == 1 && File.Exists(icon), "A live reference must retain catalog and icon without buckets");
        Check((await store.QueryAsync(StatisticsRange.CurrentSession)).Applications.Count == 0, "Keeping metadata must not retain statistics");
        clock.Set(clock.GetUtcNow().AddSeconds(1));
        var exited = Frame(software, clock.GetUtcNow());
        store.Accept(exited with { Accounting = exited.Accounting! with { Processes = [] } });
        await store.FlushAsync();
        Check(CatalogCount(path) == 0 && !File.Exists(icon), "The next flush cleans the final exited reference");
    }

    private static async Task RetryLockedIcon(string path)
    {
        var clock = new TestClock(At(25));
        await using var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(Software(path, "locked"), clock.GetUtcNow()));
        string icon = await Icon(store, "locked");
        using (var blocker = new FileStream(icon, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await store.ResetAsync(StatisticsRange.CurrentSession);
            Check(CatalogCount(path) == 0, "File contention must not roll back a successful reset");
            Check(File.Exists(icon), "The locked icon should remain for retry");
        }
        await store.FlushAsync();
        Check(!File.Exists(icon), "The next flush retries the failed deletion without waiting a day");
    }

    private static async Task RestartRetriesOrphan(string path)
    {
        var clock = new TestClock(At(24));
        string retainedIcon;
        await using (var historical = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock))
        {
            historical.Accept(Frame(Software(path, "retained"), clock.GetUtcNow()));
            retainedIcon = await Icon(historical, "retained");
        }
        clock.Set(At(25));
        var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        store.Accept(Frame(Software(path, "restart"), clock.GetUtcNow()));
        string icon = await Icon(store, "restart");
        string unrelated = Path.Combine(Path.GetDirectoryName(icon)!, "unrelated.png");
        File.WriteAllBytes(unrelated, [7, 8, 9]);
        using (var blocker = new FileStream(icon, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await store.ResetAsync(StatisticsRange.Today); }
            finally { await store.DisposeAsync(); }
            Check(File.Exists(icon), "The orphan remains locked through shutdown");
        }
        await using var reopened = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock);
        await reopened.QueryAsync(StatisticsRange.LastSevenDays);
        Check(!File.Exists(icon), "Initialization must discover an orphan without a catalog row");
        Check(File.Exists(retainedIcon) && File.Exists(unrelated), "Referenced icons and files outside the naming contract must survive");
        Check(CatalogCount(path) == 1, "The historical application must remain queryable after restart");
    }

    private static async Task<string> Icon(StatisticsService store, string name) =>
        (await store.QueryAsync(StatisticsRange.LastSevenDays)).Applications.Single(app => app.Software.DisplayName == name).Software.IconPath!;

    private static long CatalogCount(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM applications";
        return (long)command.ExecuteScalar()!;
    }

    private static SoftwareIdentity Software(string database, string name)
    {
        string icon = Path.Combine(Path.GetDirectoryName(database)!, name + ".png");
        File.WriteAllBytes(icon, [1, 2, 3]);
        string executable = $"C:\\Synthetic\\{name}.exe";
        return new("file:1:" + executable, name, name + ".exe", executable, "Synthetic", SoftwareCategory.Application, "test", icon);
    }

    private static MonitorSnapshot Frame(SoftwareIdentity software, DateTimeOffset end, bool running = false)
    {
        var identity = new ProcessIdentity(42, 1000);
        var metrics = new Dictionary<StatisticMetric, MetricAccounting>
        { [StatisticMetric.Download] = new(100, 1, 100, BytesDelta: 100, IntervalSeconds: 1) };
        var process = new ProcessSample(identity, software, 0, 1000, 0, 0, 0, 0, new Dictionary<string, double>(), 0, 0);
        return new(end, 1, new(0, 1000, 0, 0, 0, 0, 0, 0, "test"), running ? [process] : [],
            new Dictionary<string, Capability>(), false)
        { Accounting = new(Guid.NewGuid(), 1, end.AddSeconds(-1), end, 1,
            [new ProcessAccounting(identity, software, 1, metrics)], new(metrics)) };
    }

    private static DateTimeOffset At(int day) => new(2026, 11, day, 12, 0, 0, TimeSpan.Zero);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
