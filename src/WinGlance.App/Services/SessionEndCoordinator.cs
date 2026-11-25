using System.Diagnostics;

namespace WinGlance.App.Services;

/// <summary>Query is reversible; only a confirmed end stops collection. No UI work in save tasks.</summary>
internal sealed class SessionEndCoordinator(Action stop, Func<Task> flush, Func<Task> finish, Action<string> report)
{
    private readonly object _sync = new();
    private Task? _finalSave;
    private Task? _querySave;
    internal TimeSpan PreparationElapsed { get; private set; }
    internal TimeSpan LastEndElapsed { get; private set; }
    public void Query()
    {
        lock (_sync)
            if (_finalSave is null && (_querySave is null || _querySave.IsCompleted)) _querySave = Save(flush);
    }
    public Task FinishAsync()
    {
        lock (_sync)
        {
            if (_finalSave is not null) return _finalSave;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _finalSave = completion.Task;
            _ = FinishCoreAsync(completion);
            return _finalSave;
        }
    }
    public void End(bool confirmed, TimeSpan timeout)
    {
        if (!confirmed) return;
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        Task final;
        bool startedHere;
        lock (_sync)
        {
            startedHere = _finalSave is null;
            final = FinishAsync();
        }
        var remaining = timeout - elapsed.Elapsed;
        bool completed = final.IsCompleted || (remaining > TimeSpan.Zero && final.Wait(remaining));
        LastEndElapsed = elapsed.Elapsed;
        // UI-affine preparation cannot safely be interrupted or moved to a worker. It must
        // contain only short in-memory/native cleanup; all file I/O belongs in finish().
        // Include its measured duration in the budget instead of adding a fresh timeout.
        if (!completed || (startedHere && PreparationElapsed > timeout))
            report($"会话结束保存超过等待上限（同步准备 {PreparationElapsed.TotalMilliseconds:F0} ms，本次处理 {LastEndElapsed.TotalMilliseconds:F0} ms），系统可能中断未完成写入。");
    }
    private async Task FinishCoreAsync(TaskCompletionSource completion)
    {
        var preparation = Stopwatch.StartNew();
        try { stop(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { report(ex.ToString()); }
        finally { PreparationElapsed = preparation.Elapsed; }
        try { await Save(finish).ConfigureAwait(false); }
        finally { completion.TrySetResult(); }
    }
    private async Task Save(Func<Task> save)
    {
        try { await save().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { report(ex.ToString()); }
    }
}
