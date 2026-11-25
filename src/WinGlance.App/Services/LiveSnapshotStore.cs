using WinGlance.Core;

namespace WinGlance.App.Services;

internal sealed record LiveFrame(MonitorSnapshot Snapshot, IReadOnlyList<SoftwareSample> Software);
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
    public void Accept(MonitorSnapshot snapshot)
    {
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
                if (_latest is { } previous && snapshot.Timestamp < previous.Snapshot.Timestamp)
                {
                    // A wall-clock correction must not freeze the live UI until the clock catches up.
                    _system.Clear(); _history = new();
                }
            }
            else if (_latest is { } previous && snapshot.Timestamp <= previous.Snapshot.Timestamp) return;
            var software = SoftwareAggregator.Aggregate(snapshot.Processes);
            _history.Add(snapshot.Timestamp, software);
            _system.Enqueue((snapshot.Timestamp, snapshot.System.CpuPercent, snapshot.System.GpuPercent));
            while (_system.Count > 0 && (_system.Peek().Timestamp < snapshot.Timestamp.AddSeconds(-60) || _system.Count > 600)) _system.Dequeue();
            _latest = new(snapshot, software);
        }
    }
    public (DateTimeOffset Timestamp, double? Cpu, double? Gpu)[] SystemHistory()
    { lock (_sync) return _system.ToArray(); }
}
