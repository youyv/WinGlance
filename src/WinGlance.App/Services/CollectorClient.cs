using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WinGlance.Core;

namespace WinGlance.App.Services;

public sealed class CollectorClient : IDisposable
{
    private CancellationTokenSource? _cts;
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private bool _disposed;
    public event Action<MonitorSnapshot>? SnapshotReceived;
    public event Action<string>? StatusChanged;

    public async Task StartAsync(bool elevated = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        NamedPipeServerStream? localPipe = null;
        try
        {
            StatusChanged?.Invoke(elevated ? "等待增强采集授权…" : "正在连接采集器…");
            string path = Path.Combine(AppContext.BaseDirectory, "WinGlance.Collector.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("未找到采集器，请使用 scripts/build.ps1 构建完整应用。", path);
            string name = $"WinGlance.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var pipe = localPipe = _pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            using var current = Process.GetCurrentProcess();
            var info = new ProcessStartInfo(path)
            {
                Arguments = $"--pipe {name} --parent {Environment.ProcessId} --parent-start {current.StartTime.ToUniversalTime().Ticks}",
                UseShellExecute = elevated,
                CreateNoWindow = !elevated,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (elevated) info.Verb = "runas";
            _process = Process.Start(info);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                var snapshot = JsonSerializer.Deserialize<MonitorSnapshot>(line);
                if (!token.IsCancellationRequested && snapshot is not null) SnapshotReceived?.Invoke(snapshot);
            }
            if (!token.IsCancellationRequested) StatusChanged?.Invoke("采集器已断开，可点击重新连接。");
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) StatusChanged?.Invoke("采集器连接超时，请重新连接。"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!token.IsCancellationRequested) StatusChanged?.Invoke($"采集未连接：{ex.Message}");
        }
        finally
        {
            localPipe?.Dispose();
            if (ReferenceEquals(_cts, cts)) { _pipe = null; _cts = null; cts.Dispose(); }
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _pipe?.Dispose();
        _pipe = null;
        _cts?.Dispose();
        _cts = null;
        // Closing the pipe makes the collector exit through its own cleanup path.
        _process?.Dispose();
        _process = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
}
