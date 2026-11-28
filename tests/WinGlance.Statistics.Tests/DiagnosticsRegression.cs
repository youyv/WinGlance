using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Microsoft.Data.Sqlite;
using WinGlance.Core;
using WinGlance.Statistics;

internal static class DiagnosticsRegression
{
    public static async Task Run(string path)
    {
        using var listener = new CaptureListener();
        listener.EnableEvents(DiagnosticTrace.Log, EventLevel.Informational);
        var clock = new PeriodicTestClock();
        await using (var store = new StatisticsService(path, TimeZoneInfo.Utc, timeProvider: clock))
        {
            await store.QueryAsync(StatisticsRange.Today);
            await store.QueryApplicationAsync(StatisticsRange.Today, "missing");
            await store.FlushAsync();
            await store.ResetAsync(StatisticsRange.CurrentSession);
            await clock.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Tick();
            await WaitUntil(() => listener.Events.Any(e => e.EventId == 2 && e.Text(0) == "finished" && e.Text(1) == "periodic"));

            using var external = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString());
            external.Open();
            using (var command = external.CreateCommand())
            {
                command.CommandText = "CREATE TRIGGER diagnostic_failure BEFORE UPDATE ON meta WHEN NEW.name='lastSaved' BEGIN SELECT RAISE(ABORT,'diagnostic failure'); END;";
                command.ExecuteNonQuery();
            }
            try
            {
                await store.FlushAsync();
                throw new InvalidOperationException("Expected flush failure was not raised.");
            }
            catch (SqliteException) { }
            finally
            {
                using var command = external.CreateCommand();
                command.CommandText = "DROP TRIGGER diagnostic_failure";
                command.ExecuteNonQuery();
            }
            try
            {
                await store.QueryAsync((StatisticsRange)999);
                throw new InvalidOperationException("Expected invalid query failure was not raised.");
            }
            catch (ArgumentOutOfRangeException) { }
        }

        var events = listener.Events.ToArray();
        Require(!events.Any(e => e.EventId == 0), "EventSource emitted a manifest/payload error.");
        var flush = events.Where(e => e.EventId == 2).ToArray();
        foreach (string reason in new[] { "manual", "periodic", "reset", "exit" })
        {
            var completed = flush.FirstOrDefault(e => e.Text(0) == "finished" && e.Text(1) == reason)
                ?? throw new InvalidOperationException("Missing successful " + reason + " flush.");
            long id = completed.Number(2);
            var phases = flush.Where(e => e.Number(2) == id).Select(e => e.Text(0)).ToArray();
            string[] expected = reason == "exit"
                ? ["start", "transaction", "buckets", "metadata", "compact", "catalog", "commit", "cache", "finished"]
                : ["queued", "start", "transaction", "buckets", "metadata", "compact", "catalog", "commit", "cache", "finished"];
            Require(phases.SequenceEqual(expected), "Flush phases must share an ID and preserve actual execution order: " + reason);
        }
        var failure = flush.Single(e => e.Text(0) == "failed");
        Require(!flush.Any(e => e.Number(2) == failure.Number(2) && e.Text(0) == "finished"),
            "Failed flush must not emit successful completion.");
        var work = events.Where(e => e.EventId == 3).ToArray();
        Require(work.Any(e => e.Text(0) == "failed" && e.Text(1) == "flush" && e.Text(5) == nameof(SqliteException)),
            "Flush request failure needs its exception type.");
        Require(work.Any(e => e.Text(0) == "failed" && e.Text(1) == "query" && e.Text(5) == nameof(ArgumentOutOfRangeException)),
            "Query failure needs its exception type.");
        foreach (string operation in new[] { "query", "queryApplication" })
        {
            var finished = work.First(e => e.Text(0) == "finished" && e.Text(1) == operation);
            long id = finished.Number(2);
            var phases = work.Where(e => e.Number(2) == id).ToArray();
            Require(phases.Select(e => e.Text(0)).SequenceEqual(new[] { "queued", "start", "finished" }),
                "Queries must expose separate queue and execution timing.");
            Require(phases.All(e => Convert.ToDouble(e.Payload[3]) >= 0), "Diagnostic durations must be nonnegative.");
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed record Captured(int EventId, object?[] Payload)
    {
        public string Text(int index) => Convert.ToString(Payload[index]) ?? "";
        public long Number(int index) => Convert.ToInt64(Payload[index]);
    }

    private sealed class CaptureListener : EventListener
    {
        public ConcurrentQueue<Captured> Events { get; } = new();
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name == "WinGlance-Diagnostics")
                Events.Enqueue(new(eventData.EventId, eventData.Payload?.ToArray() ?? []));
        }
    }

    private sealed class PeriodicTestClock : TimeProvider
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new(callback, state); Ready.TrySetResult(); return _timer;
        }
        public void Tick() => _timer!.Tick();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Tick() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
