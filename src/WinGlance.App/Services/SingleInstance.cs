using System.Security.Principal;

namespace WinGlance.App.Services;

/// <summary>A per-user owner prevents duplicate collection and writers, including another desktop session.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _wait;
    private SingleInstance(Mutex mutex, EventWaitHandle activation) { _mutex = mutex; _activation = activation; }
    public static SingleInstance? Acquire()
    {
        string key = @"Global\WinGlance." + WindowsIdentity.GetCurrent().User!.Value;
        var mutex = new Mutex(true, key + ".owner", out bool created);
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, key + ".activate");
        bool acquired = created;
        if (!acquired) { try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; } }
        if (acquired) return new SingleInstance(mutex, activation);
        activation.Set(); activation.Dispose(); mutex.Dispose(); return null;
    }
    public void Listen(Action show) => _wait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, timedOut) => { if (!timedOut) show(); }, null, Timeout.Infinite, false);
    public void Dispose()
    {
        _wait?.Unregister(null); _activation.Dispose();
        _mutex.ReleaseMutex(); _mutex.Dispose();
    }
}
