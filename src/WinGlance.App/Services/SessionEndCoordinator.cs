namespace WinGlance.App.Services;

/// <summary>Query is reversible; only a confirmed end stops collection. No UI work in save tasks.</summary>
internal sealed class SessionEndCoordinator(Action stop, Func<Task> flush, Func<Task> finish, Action<string> report)
{
    private readonly object _sync = new();
    private Task? _finalSave;
    private Task? _querySave;
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
            stop();
            return _finalSave = Save(finish);
        }
    }
    public void End(bool confirmed, TimeSpan timeout)
    {
        if (confirmed && !FinishAsync().Wait(timeout)) report("会话结束保存超过等待上限，系统可能中断未完成写入。");
    }
    private async Task Save(Func<Task> save)
    {
        try { await save().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { report(ex.ToString()); }
    }
}
