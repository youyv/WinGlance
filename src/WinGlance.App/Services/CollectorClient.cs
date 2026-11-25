using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using WinGlance.Core;

namespace WinGlance.App.Services;

public sealed record CollectorConnection(bool Connecting, bool Connected, bool Enhanced, string Message, string? Notice = null);

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
        CancellationTokenSource? cts = null;
        lock (_sync)
        {
            if (_disposed || _connection.Connecting || (!forceReconnect && _connection.Connected && _connection.Enhanced)) return Task.CompletedTask;
            // Check account eligibility only; ShellExecute/UAC still decides whether to grant elevation.
            if (elevated && !_canElevate())
            {
                const string message = "增强采集需要当前 Windows 账户属于管理员组；暂不支持输入另一账户的凭据。普通采集继续运行。";
                _connection = _connection with { Notice = message };
            }
            else
            {
                StopLocked();
                cts = _cts = new CancellationTokenSource();
                _connection = new(true, false, elevated, elevated ? "等待当前账户的增强采集授权…" : "正在连接采集器…");
            }
        }
        ConnectionChanged?.Invoke();
        StatusChanged?.Invoke(Connection.Message);
        return cts is null ? Task.CompletedTask : RunAsync(cts, elevated);
    }

    public void DismissNotice()
    {
        lock (_sync) _connection = _connection with { Notice = null };
        ConnectionChanged?.Invoke();
    }

    internal static bool CanElevateCurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return CanRequestElevation(identity);
    }

    internal static bool CanRequestElevation(ClaimsIdentity identity)
    {
        string administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        // WindowsIdentity.Groups omits deny-only groups from a UAC-filtered admin token.
        // WindowsIdentity.Claims retains them, so an unelevated administrator can request consent.
        return identity.Claims.Any(claim => claim.Value == administrators &&
            (claim.Type == ClaimTypes.GroupSid || claim.Type == ClaimTypes.DenyOnlySid));
    }

    private async Task RunAsync(CancellationTokenSource cts, bool elevated)
    {
        var token = cts.Token;
        NamedPipeServerStream? pipe = null;
        Process? process = null;
        CancellationTokenSource? startup = null;
        CancellationTokenSource? fallback = null;
        bool received = false;
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
            startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            var exited = process is null ? Task.Delay(Timeout.Infinite, startup.Token) : process.WaitForExitAsync(startup.Token);
            await AwaitStartup(pipe.WaitForConnectionAsync(startup.Token), exited, startup.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            bool first = true;
            while (!token.IsCancellationRequested)
            {
                var read = reader.ReadLineAsync(first ? startup.Token : token).AsTask();
                if (first) await AwaitStartup(read, exited, startup.Token).ConfigureAwait(false);
                var line = await read.ConfigureAwait(false);
                if (line is null) { if (first) finalMessage = "采集器在发送首份数据前断开。"; break; }
                var snapshot = JsonSerializer.Deserialize<MonitorSnapshot>(line);
                if (token.IsCancellationRequested || snapshot is null) continue;
                if (first)
                {
                    first = false;
                    received = true;
                    if (!SetConnection(cts, new(false, true, snapshot.IsElevated,
                            snapshot.IsElevated ? "增强采集中（当前账户）" : "普通采集中；可授权当前账户启用增强采集"))) break;
                    startup.Cancel();
                }
                lock (_sync) { if (!ReferenceEquals(_cts, cts) || _disposed) break; }
                SnapshotReceived?.Invoke(snapshot);
            }
        }
        catch (OperationCanceledException) { finalMessage = "采集器连接超时，请重新连接。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            finalMessage = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "已取消增强采集授权。" : $"采集未连接：{ex.Message}";
        }
        finally
        {
            startup?.Cancel(); startup?.Dispose(); pipe?.Dispose(); process?.Dispose();
            bool notify;
            lock (_sync)
            {
                notify = ReferenceEquals(_cts, cts) && !_disposed;
                if (ReferenceEquals(_cts, cts))
                {
                    _pipe = null; _cts = null;
                    if (notify && elevated && !received && !token.IsCancellationRequested)
                    {
                        fallback = _cts = new CancellationTokenSource();
                        _connection = new(true, false, false, "正在恢复普通采集…", finalMessage + " 已自动尝试恢复普通采集。");
                    }
                    else _connection = new(false, false, false, finalMessage, _connection.Notice);
                }
            }
            cts.Dispose();
            if (notify) { ConnectionChanged?.Invoke(); StatusChanged?.Invoke(Connection.Message); }
        }
        if (fallback is not null) await RunAsync(fallback, false).ConfigureAwait(false);
    }

    private static async Task AwaitStartup(Task operation, Task exited, CancellationToken token)
    {
        if (await Task.WhenAny(operation, exited).ConfigureAwait(false) == exited)
        {
            token.ThrowIfCancellationRequested();
            await exited.ConfigureAwait(false);
            throw new IOException("采集器在完成连接前已退出，请检查采集器错误日志。");
        }
        await operation.ConfigureAwait(false);
    }

    private bool SetConnection(CancellationTokenSource cts, CollectorConnection connection)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_cts, cts) || _disposed) return false;
            _connection = connection with { Notice = _connection.Notice };
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
