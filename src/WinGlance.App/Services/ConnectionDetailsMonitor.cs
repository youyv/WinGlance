using WinGlance.Core;

namespace WinGlance.App.Services;

internal sealed record ConnectionDetailsSnapshot(long Generation, string SelectionKey, ConnectionReadResult Result);

internal sealed class ConnectionDetailsMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<ProcessIdentity>, CancellationToken, Task<ConnectionReadResult>> _read;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private CancellationTokenSource? _query;
    private string? _selection;
    private ProcessIdentity[] _identities = [];
    private long _generation;
    private bool _disposed;

    internal ConnectionDetailsMonitor() : this(IpHelperConnections.ReadAsync, TimeSpan.FromSeconds(3)) { }
    internal ConnectionDetailsMonitor(Func<IReadOnlyList<ProcessIdentity>, CancellationToken, Task<ConnectionReadResult>> read, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _read = read; _interval = interval;
        _loop = Task.Run(RunAsync);
    }

    internal event Action<ConnectionDetailsSnapshot>? Updated;

    // The overview calls this with current group membership. Ordinary metric refreshes do not
    // wake the reader; only a changed selection or process identity changes its generation.
    internal long Activate(string selectionKey, IEnumerable<ProcessIdentity> identities)
    {
        ArgumentException.ThrowIfNullOrEmpty(selectionKey);
        var members = identities.Distinct().OrderBy(x => x.Pid).ThenBy(x => x.StartTimeUtcTicks).ToArray();
        lock (_sync)
        {
            if (_disposed) return _generation;
            if (_selection == selectionKey && _identities.SequenceEqual(members)) return _generation;
            _selection = selectionKey; _identities = members; _generation++;
            _query?.Cancel(); Wake();
            return _generation;
        }
    }

    internal void Deactivate()
    {
        lock (_sync)
        {
            if (_disposed || _selection is null) return;
            _selection = null; _identities = []; _generation++;
            _query?.Cancel(); Wake();
        }
    }

    // Recheck inside the UI dispatcher callback: selection may change after an event was queued.
    internal bool IsCurrent(ConnectionDetailsSnapshot snapshot)
    {
        lock (_sync) return !_disposed && _selection is not null && _generation == snapshot.Generation && _selection == snapshot.SelectionKey;
    }

    private void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                string? selection; ProcessIdentity[] identities; long generation;
                lock (_sync)
                {
                    _wake.Wait(0);
                    selection = _selection; identities = _identities; generation = _generation;
                }
                if (selection is null)
                {
                    await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                using var query = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                lock (_sync)
                {
                    if (_generation != generation || _disposed) continue;
                    _query = query;
                }
                ConnectionReadResult? result = null;
                try { result = await _read(identities, query.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (query.IsCancellationRequested) { }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result = new(DateTimeOffset.Now, [], identities.Length,
                        [$"连接查询（{ex.GetType().Name}）"], false, 0);
                }
                finally
                {
                    lock (_sync) { if (ReferenceEquals(_query, query)) _query = null; }
                }
                if (result is not null)
                {
                    var snapshot = new ConnectionDetailsSnapshot(generation, selection, result);
                    if (IsCurrent(snapshot)) Updated?.Invoke(snapshot);
                }
                await _wake.WaitAsync(_interval, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            lock (_sync)
            {
                _disposed = true; _selection = null; _identities = []; _generation++;
                _wake.Dispose(); _lifetime.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _selection = null; _identities = []; _generation++;
            _lifetime.Cancel(); _query?.Cancel();
        }
    }

    internal Task Completion => _loop;
}
