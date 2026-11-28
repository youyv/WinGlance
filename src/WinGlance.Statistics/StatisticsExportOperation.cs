namespace WinGlance.Statistics;

/// <summary>Cancellation and commit share one gate; the independent writer never needs the UI dispatcher.</summary>
public sealed class StatisticsExportOperation
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<StatisticsExportResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StatisticsExportPhase _phase = StatisticsExportPhase.Selecting;
    private bool _cancelRequested;
    private Task<StatisticsExportResult>? _writeTask;
    public StatisticsExportOperation(StatisticsExportRequest request, StatisticsExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        Request = request; Format = format;
    }
    public StatisticsExportRequest Request { get; }
    public StatisticsExportFormat Format { get; }
    public StatisticsExportPhase Phase { get { lock (_gate) return _phase; } }
    public CancellationToken Token => _cancellation.Token;
    public Task<StatisticsExportResult> Completion => _completion.Task;
    public Task<StatisticsExportResult>? WriteTask { get { lock (_gate) return _writeTask; } }
    public bool TryBeginCapture()
    {
        lock (_gate)
        {
            if (_phase != StatisticsExportPhase.Selecting || _cancelRequested) return false;
            _phase = StatisticsExportPhase.Capturing;
            return true;
        }
    }
    public bool TryCancel()
    {
        lock (_gate)
        {
            if (_cancelRequested || _phase is StatisticsExportPhase.Committing or StatisticsExportPhase.Completed or StatisticsExportPhase.Cancelled or StatisticsExportPhase.Failed) return false;
            _cancelRequested = true;
            if (_writeTask is null)
            {
                _phase = StatisticsExportPhase.Cancelled;
                _completion.TrySetResult(new(_phase));
            }
        }
        _cancellation.Cancel();
        return true;
    }
    public void FailBeforeWrite(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            if (_phase is not (StatisticsExportPhase.Selecting or StatisticsExportPhase.Capturing)) return;
            _phase = error is OperationCanceledException && _cancellation.IsCancellationRequested
                ? StatisticsExportPhase.Cancelled : StatisticsExportPhase.Failed;
            _completion.TrySetResult(new(_phase, Error: _phase == StatisticsExportPhase.Failed ? error.Message : null));
        }
    }
    internal Task<StatisticsExportResult> StartWriting(Func<Task<StatisticsExportResult>> writer)
    {
        lock (_gate)
        {
            if (_writeTask is not null) return _writeTask;
            if (_phase == StatisticsExportPhase.Cancelled) return _completion.Task;
            if (_phase != StatisticsExportPhase.Capturing) throw new InvalidOperationException("导出尚未完成快照捕获。");
            _phase = StatisticsExportPhase.Writing;
            _writeTask = Task.Run(async () =>
            {
                StatisticsExportResult result;
                try { result = await writer().ConfigureAwait(false); }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { result = new(StatisticsExportPhase.Cancelled); }
                catch (Exception error) when (error is not OutOfMemoryException) { result = new(StatisticsExportPhase.Failed, Error: error.Message); }
                lock (_gate) { _phase = result.Phase; _completion.TrySetResult(result); }
                return result;
            });
            return _writeTask;
        }
    }
    internal bool TryBeginCommit()
    {
        lock (_gate)
        {
            if (_phase != StatisticsExportPhase.Writing || _cancelRequested) return false;
            _phase = StatisticsExportPhase.Committing;
            return true;
        }
    }
}
