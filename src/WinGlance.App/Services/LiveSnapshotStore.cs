using WinGlance.Core;

namespace WinGlance.App.Services;

internal sealed record LiveFrame(MonitorSnapshot Snapshot, IReadOnlyList<SoftwareSample> Software, long Generation = 0);
internal sealed record PreparedLiveFrame(MonitorSnapshot Snapshot, IReadOnlyList<SoftwareSample> Software);
internal sealed class LiveSnapshotStore
{
    private readonly object _sync = new();
    private readonly Queue<(DateTimeOffset Timestamp, double? Cpu, double? Gpu)> _system = [];
    private LiveFrame? _latest;
    private HistoryBuffer _history = new();
    private Guid? _run;
    private long _sequence = -1;
    private readonly Queue<Guid> _retiredRuns = [];
    public HistoryBuffer History { get { lock (_sync) return _history; } }
    public LiveFrame? Latest { get { lock (_sync) return _latest; } }
    internal static PreparedLiveFrame Prepare(MonitorSnapshot snapshot) => new(snapshot, SoftwareAggregator.Aggregate(snapshot.Processes));
    // CollectorClient has already checked its session under its lifecycle gate. No aggregation,
    // file access or callbacks occur here, and no store operation calls back into the client.
    internal void CommitPrepared(long generation, PreparedLiveFrame prepared)
    {
        lock (_sync) Commit(generation, prepared);
    }
    public void Accept(MonitorSnapshot snapshot)
    {
        var prepared = Prepare(snapshot);
        lock (_sync)
        {
            if (snapshot.Accounting is { } accounting)
            {
                if (accounting.CollectorRunId == _run && accounting.Sequence <= _sequence || _retiredRuns.Contains(accounting.CollectorRunId)) return;
                if (_run is { } oldRun && oldRun != accounting.CollectorRunId)
                {
                    _retiredRuns.Enqueue(oldRun);
                    while (_retiredRuns.Count > 32) _retiredRuns.Dequeue();
                }
                _run = accounting.CollectorRunId; _sequence = accounting.Sequence;
            }
            else if (_latest is { } previous && snapshot.Timestamp <= previous.Snapshot.Timestamp) return;
            Commit(0, prepared);
        }
    }
    private void Commit(long generation, PreparedLiveFrame prepared)
    {
        var snapshot = prepared.Snapshot;
        if (_latest is { } previous && snapshot.Timestamp < previous.Snapshot.Timestamp)
        {
            // Client progress is monotonic even when the wall clock moves backwards.
            _system.Clear(); _history = new();
        }
        _history.Add(snapshot.Timestamp, prepared.Software);
        _system.Enqueue((snapshot.Timestamp, snapshot.System.CpuPercent, snapshot.System.GpuPercent));
        while (_system.Count > 0 && (_system.Peek().Timestamp < snapshot.Timestamp.AddSeconds(-60) || _system.Count > 600)) _system.Dequeue();
        _latest = new(snapshot, prepared.Software, generation);
    }
    public (DateTimeOffset Timestamp, double? Cpu, double? Gpu)[] SystemHistory()
    { lock (_sync) return _system.ToArray(); }
}
