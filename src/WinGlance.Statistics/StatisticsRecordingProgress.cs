namespace WinGlance.Statistics;

public enum StatisticsRecordingPhase
{
    WaitingForFirstInterval,
    WaitingAfterReset,
    WaitingForBaseline,
    Recording
}

/// <summary>Current-process accounting progress; never persisted or part of a cached query/export.</summary>
public sealed record StatisticsRecordingProgress(StatisticsRecordingPhase Phase, TimeSpan Age,
    DateTimeOffset? LastRecordedUtc)
{
    public bool IsStalled => Age > TimeSpan.FromSeconds(6);
}

internal sealed class StatisticsRecordingTracker
{
    private sealed record Point(StatisticsRecordingPhase Phase, long Since, DateTimeOffset? LastRecordedUtc);
    private readonly TimeProvider _clock;
    private Point _point;

    internal StatisticsRecordingTracker(TimeProvider clock)
    {
        _clock = clock;
        _point = new(StatisticsRecordingPhase.WaitingForFirstInterval, clock.GetTimestamp(), null);
    }

    internal StatisticsRecordingProgress Read()
    {
        var point = Volatile.Read(ref _point);
        var elapsed = _clock.GetElapsedTime(point.Since, _clock.GetTimestamp());
        return new(point.Phase, elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed, point.LastRecordedUtc);
    }

    // Called only by the statistics worker after a frame passes sequence validation.
    internal void BeginBaseline(bool isNewCollectorRun)
    {
        var point = Volatile.Read(ref _point);
        // Repeated baseline frames cannot keep accounting healthy forever. A genuinely new
        // collector run receives one warmup window even if the previous run never recovered.
        if (point.Phase == StatisticsRecordingPhase.WaitingForBaseline && !isNewCollectorRun) return;
        Volatile.Write(ref _point, new(StatisticsRecordingPhase.WaitingForBaseline,
            _clock.GetTimestamp(), point.LastRecordedUtc));
    }

    internal void Recorded(DateTimeOffset endUtc) => Volatile.Write(ref _point,
        new(StatisticsRecordingPhase.Recording, _clock.GetTimestamp(), endUtc));

    internal void Reset() => Volatile.Write(ref _point,
        new(StatisticsRecordingPhase.WaitingAfterReset, _clock.GetTimestamp(), null));
}
