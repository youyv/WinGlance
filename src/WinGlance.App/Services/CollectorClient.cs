using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using WinGlance.Core;

namespace WinGlance.App.Services;

public sealed record CollectorConnection(bool Connecting, bool Connected, bool Enhanced, string Message);

public sealed class CollectorClient : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private NamedPipeServerStream? _pipe;
    private bool _disposed;
    private CollectorConnection _connection = new(false, false, false, "尚未连接采集器");
    private readonly Func<bool> _canElevate;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<Process?>> _launch;
    private readonly string _collectorPath;
    public CollectorClient() : this(Path.Combine(AppContext.BaseDirectory, "WinGlance.Collector.exe"), CanElevateCurrentAccount,
        (info, token) => Task.Run(() => Process.Start(info), token)) { }
    internal CollectorClient(string collectorPath, Func<bool> canElevate, Func<ProcessStartInfo, CancellationToken, Task<Process?>> launch)
    { _collectorPath = collectorPath; _canElevate = canElevate; _launch = launch; }
    public CollectorConnection Connection { get { lock (_sync) return _connection; } }
    public event Action<MonitorSnapshot>? SnapshotReceived;
    public event Action<string>? StatusChanged;
    public event Action? ConnectionChanged;

    public Task StartAsync(bool elevated = false, bool forceReconnect = false)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_disposed || _connection.Connecting || (!forceReconnect && _connection.Connected && _connection.Enhanced)) return Task.CompletedTask;
            // A filtered admin token still contains this SID; IsInRole alone would reject same-account UAC.
            if (elevated && !_canElevate())
            {
                const string message = "增强采集需要当前 Windows 账户属于管理员组；暂不支持输入另一账户的凭据。普通采集继续运行。";
                _connection = _connection with { Message = message };
                StatusChanged?.Invoke(message); ConnectionChanged?.Invoke();
                return Task.CompletedTask;
            }
            StopLocked();
            cts = _cts = new CancellationTokenSource();
            _connection = new(true, false, elevated, elevated ? "等待当前账户的增强采集授权…" : "正在连接采集器…");
        }
        ConnectionChanged?.Invoke();
        StatusChanged?.Invoke(Connection.Message);
        return RunAsync(cts, elevated);
    }

    private static bool CanElevateCurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        return identity.Groups?.Contains(administrators) == true;
    }

    private async Task RunAsync(CancellationTokenSource cts, bool elevated)
    {
        var token = cts.Token;
        NamedPipeServerStream? pipe = null;
        Process? process = null;
        string finalMessage = "采集器已断开，可点击重新连接。";
        try
        {
            string path = _collectorPath;
            if (!File.Exists(path)) throw new FileNotFoundException("未找到采集器，请使用 scripts/build.ps1 构建完整应用。", path);
            string name = $"WinGlance.{Environment.ProcessId}.{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            lock (_sync)
            {
                if (!ReferenceEquals(_cts, cts)) return;
                _pipe = pipe;
            }
            using var current = Process.GetCurrentProcess();
            var info = new ProcessStartInfo(path)
            {
                Arguments = $"--pipe {name} --parent {Environment.ProcessId} --parent-start {current.StartTime.ToUniversalTime().Ticks}",
                UseShellExecute = elevated, CreateNoWindow = !elevated,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory
            };
            if (elevated) info.Verb = "runas";
            // Keep both pages responsive while ShellExecute waits for UAC.
            process = await _launch(info, token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            bool first = true;
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(first ? timeout.Token : token).ConfigureAwait(false);
                if (line is null) break;
                var snapshot = JsonSerializer.Deserialize<MonitorSnapshot>(line);
                if (token.IsCancellationRequested || snapshot is null) continue;
                if (first)
                {
                    first = false;
                    if (!SetConnection(cts, new(false, true, snapshot.IsElevated,
                            snapshot.IsElevated ? "增强采集中（当前账户）" : "普通采集中；可授权当前账户启用增强采集"))) break;
                }
                SnapshotReceived?.Invoke(snapshot);
            }
        }
        catch (OperationCanceledException) { finalMessage = "采集器连接超时，请重新连接。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            finalMessage = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "已取消增强采集授权，可重新连接普通采集或重试。" : $"采集未连接：{ex.Message}";
        }
        finally
        {
            pipe?.Dispose(); process?.Dispose();
            bool notify;
            lock (_sync)
            {
                notify = ReferenceEquals(_cts, cts) && !_disposed;
                if (ReferenceEquals(_cts, cts))
                {
                    _pipe = null; _cts = null;
                    _connection = new(false, false, false, finalMessage);
                }
            }
            cts.Dispose();
            if (notify) { ConnectionChanged?.Invoke(); StatusChanged?.Invoke(finalMessage); }
        }
    }

    private bool SetConnection(CancellationTokenSource cts, CollectorConnection connection)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_cts, cts) || _disposed) return false;
            _connection = connection;
        }
        ConnectionChanged?.Invoke();
        return true;
    }

    private void StopLocked()
    {
        _cts?.Cancel(); _cts = null;
        _pipe?.Dispose(); _pipe = null;
        // Each read loop owns/disposes its CTS and process wrapper. Pipe closure stops that collector.
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; StopLocked(); }
    }
}
