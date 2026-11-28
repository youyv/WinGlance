using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using WinGlance.Core;
using WinGlance.Statistics;

namespace WinGlance.App.Services;

public enum CollectionFreshness { WaitingForFirstFrame, Fresh, Stalled, WaitingAfterResume }
public sealed record CollectorConnection(bool Connecting, bool Connected, bool Enhanced, string Message, string? Notice = null,
    long Generation = 0, CollectionFreshness Freshness = CollectionFreshness.WaitingForFirstFrame,
    DateTimeOffset? LastObservedUtc = null, DateTimeOffset? LastReceivedUtc = null);
public sealed record CollectorObservation(long Generation, MonitorSnapshot Snapshot, long ReceivedTimestamp);
internal sealed record CollectorReadState(CollectorConnection Connection, LiveFrame? Frame);

public sealed class CollectorClient : IDisposable
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(6);
    private readonly object _sync = new();
    private SessionState? _session;
    private long _generation;
    private bool _disposed, _suspended;
    private CollectorConnection _connection = new(false, false, false, "尚未连接采集器");
    private readonly Func<bool> _canElevate;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<Process?>> _launch;
    private readonly string _collectorPath;
    private readonly TimeProvider _clock;
    private readonly LiveSnapshotStore _live;
    private readonly StatisticsService? _statistics;

    public CollectorClient() : this(new LiveSnapshotStore(), null) { }
    internal CollectorClient(LiveSnapshotStore live, StatisticsService? statistics, TimeProvider? timeProvider = null)
        : this(Path.Combine(AppContext.BaseDirectory, "WinGlance.Collector.exe"), CanElevateCurrentAccount,
            (info, token) => Task.Run(() => Process.Start(info), token), timeProvider, live, statistics) { }
    internal CollectorClient(string collectorPath, Func<bool> canElevate,
        Func<ProcessStartInfo, CancellationToken, Task<Process?>> launch, TimeProvider? timeProvider = null,
        LiveSnapshotStore? live = null, StatisticsService? statistics = null)
    {
        _collectorPath = collectorPath; _canElevate = canElevate; _launch = launch;
        _clock = timeProvider ?? TimeProvider.System; _live = live ?? new(); _statistics = statistics;
    }

    public CollectorConnection Connection { get { lock (_sync) return _connection; } }
    internal CollectorReadState ReadState() { lock (_sync) return new(_connection, _live.Latest); }
    // Notifications occur after admission and can arrive late. UI consumers re-read ReadState;
    // they must never enqueue or commit the event payload a second time.
    public event Action<CollectorObservation>? SnapshotReceived;
    public event Action<string>? StatusChanged;
    public event Action? ConnectionChanged;

    public Task StartAsync(bool elevated = false, bool forceReconnect = false)
    {
        SessionState? session = null, previous = null;
        lock (_sync)
        {
            if (_disposed || _connection.Connecting || (!forceReconnect && _connection.Connected && _connection.Enhanced)) return Task.CompletedTask;
            if (elevated && !_canElevate())
            {
                string status = !_connection.Connected ? "可点击恢复普通采集。" :
                    _connection.Freshness == CollectionFreshness.Fresh ? "普通采集继续运行。" : "普通采集暂未更新，可手动恢复。";
                _connection = _connection with { Notice = "增强采集需要当前 Windows 账户属于管理员组；暂不支持输入另一账户的凭据。" + status };
            }
            else
            {
                previous = _session;
                session = NewSessionLocked(elevated);
                _connection = new(true, false, elevated, elevated ? "等待当前账户的增强采集授权…" : "正在连接采集器…",
                    Generation: session.Generation);
            }
        }
        previous?.Stop();
        NotifyConnection();
        return session is null ? Task.CompletedTask : RunAsync(session);
    }

    public void DismissNotice()
    {
        lock (_sync) { if (_disposed) return; _connection = _connection with { Notice = null }; }
        NotifyConnection();
    }

    public void OnSuspending()
    {
        bool changed = false;
        lock (_sync)
        {
            if (_disposed || _suspended) return;
            _suspended = true;
            if (_session is { } session)
            {
                session.ProgressAfterSuspend = false;
                session.ResumePending = true;
                session.Timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                if (session.HasFrame)
                {
                    _connection = _connection with { Freshness = CollectionFreshness.WaitingAfterResume };
                    changed = true;
                }
            }
        }
        if (changed) NotifyConnection();
    }

    public void OnResumed()
    {
        bool changed = false;
        lock (_sync)
        {
            if (_disposed || !_suspended) return;
            _suspended = false;
            if (_session is { } session && session.ResumePending)
            {
                session.ResumePending = false;
                if (!session.HasFrame) return;
                if (!session.ProgressAfterSuspend)
                {
                    _connection = _connection with { Freshness = CollectionFreshness.WaitingAfterResume };
                    session.CheckFromTimestamp = _clock.GetTimestamp();
                    changed = true;
                }
                else session.CheckFromTimestamp = session.LastProgressTimestamp;
                session.Timer!.Change(StaleAfter, Timeout.InfiniteTimeSpan);
            }
        }
        if (changed) NotifyConnection();
    }

    internal static bool CanElevateCurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return CanRequestElevation(identity);
    }
    internal static bool CanRequestElevation(ClaimsIdentity identity)
    {
        string administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        return identity.Claims.Any(claim => claim.Value == administrators &&
            (claim.Type == ClaimTypes.GroupSid || claim.Type == ClaimTypes.DenyOnlySid));
    }

    private SessionState NewSessionLocked(bool elevated)
    {
        var session = new SessionState(++_generation, elevated) { ResumePending = _suspended };
        session.Timer = _clock.CreateTimer(_ => CheckFreshness(session), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return _session = session;
    }
    private bool IsCurrentLocked(SessionState session) => !_disposed && ReferenceEquals(_session, session) && !session.Cts.IsCancellationRequested;

    private void CheckFreshness(SessionState session)
    {
        bool changed = false;
        lock (_sync)
        {
            if (!IsCurrentLocked(session) || !session.HasFrame || _suspended ||
                _connection.Freshness == CollectionFreshness.Stalled) return;
            var elapsed = _clock.GetElapsedTime(session.CheckFromTimestamp, _clock.GetTimestamp());
            if (elapsed < StaleAfter)
                session.Timer!.Change(StaleAfter - elapsed, Timeout.InfiniteTimeSpan);
            else
            {
                _connection = _connection with { Freshness = CollectionFreshness.Stalled };
                session.Timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                changed = true;
            }
        }
        if (changed) NotifyConnection();
    }

    private static bool IsProgress(SessionState session, MonitorSnapshot snapshot) => snapshot.Accounting is not { } frame ||
        frame.Sequence >= 0 && (session.CollectorRunId is null ||
            session.CollectorRunId == frame.CollectorRunId && frame.Sequence > session.LastSequence);

    internal bool TryAcceptObservation(long generation, MonitorSnapshot snapshot, out CollectorObservation? observation, out bool stateChanged)
    {
        observation = null; stateChanged = false;
        SessionState session;
        lock (_sync)
        {
            if (_session is not { } current || current.Generation != generation || !IsCurrentLocked(current) || !IsProgress(current, snapshot)) return false;
            session = current;
        }
        // Heavy aggregation stays outside the lifecycle gate. The second check rejects a
        // frame prepared across a connection replacement or disposal.
        long prepareStarted = DiagnosticTrace.Start();
        var prepared = LiveSnapshotStore.Prepare(snapshot);
        DiagnosticTrace.End("app.acceptPrepare", prepareStarted, count: snapshot.Processes.Count);
        long gateStarted = 0;
        try
        {
            lock (_sync)
            {
                gateStarted = DiagnosticTrace.Start();
                if (!IsCurrentLocked(session) || !IsProgress(session, snapshot)) return false;
                long received = _clock.GetTimestamp();
                bool first = !session.HasFrame;
                stateChanged = first || _connection.Freshness != CollectionFreshness.Fresh;
                // Concrete, bounded in-memory consumers only: no arbitrary event callback, I/O,
                // dispatcher wait or async operation is allowed within this gate.
                _statistics?.Accept(snapshot); // Queue failure is independent of channel freshness.
                _live.CommitPrepared(session.Generation, prepared);
                if (snapshot.Accounting is { } frame)
                { session.CollectorRunId = frame.CollectorRunId; session.LastSequence = frame.Sequence; }
                session.HasFrame = true;
                session.ProgressAfterSuspend = true;
                session.LastProgressTimestamp = session.CheckFromTimestamp = received;
                _connection = new(false, true, snapshot.IsElevated,
                snapshot.IsElevated ? "增强采集中（当前账户）" : "普通采集中；可授权当前账户启用增强采集",
                _connection.Notice, session.Generation, CollectionFreshness.Fresh, snapshot.Timestamp, _clock.GetUtcNow());
                if (stateChanged && !_suspended) session.Timer!.Change(StaleAfter, Timeout.InfiniteTimeSpan);
                observation = new(session.Generation, snapshot, received);
                return true;
            }
        }
        finally { DiagnosticTrace.End("app.acceptGate", gateStarted, count: snapshot.Processes.Count); }
    }

    private async Task RunAsync(SessionState session)
    {
        var token = session.Cts.Token;
        NamedPipeServerStream? pipe = null;
        Process? process = null;
        CancellationTokenSource? startup = null;
        SessionState? fallback = null;
        string finalMessage = "采集器已断开，可点击重新连接。";
        try
        {
            if (!File.Exists(_collectorPath)) throw new FileNotFoundException("未找到采集器，请使用 scripts/build.ps1 构建完整应用。", _collectorPath);
            string name = $"WinGlance.{Environment.ProcessId}.{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
            lock (_sync)
            {
                if (!IsCurrentLocked(session)) return;
                session.Pipe = pipe;
            }
            using var current = Process.GetCurrentProcess();
            var info = new ProcessStartInfo(_collectorPath)
            {
                Arguments = $"--pipe {name} --parent {Environment.ProcessId} --parent-start {current.StartTime.ToUniversalTime().Ticks}",
                UseShellExecute = session.Elevated, CreateNoWindow = !session.Elevated,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory
            };
            if (session.Elevated) info.Verb = "runas";
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
                if (snapshot is null || !TryAcceptObservation(session.Generation, snapshot, out var observation, out bool stateChanged)) continue;
                if (first) { first = false; startup.Cancel(); }
                if (stateChanged) NotifyConnection();
                SnapshotReceived?.Invoke(observation!);
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
                notify = ReferenceEquals(_session, session) && !_disposed;
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    if (notify && session.Elevated && !session.HasFrame && !token.IsCancellationRequested)
                    {
                        fallback = NewSessionLocked(false);
                        _connection = new(true, false, false, "正在恢复普通采集…",
                            finalMessage + " 已自动尝试恢复普通采集。", fallback.Generation);
                    }
                    else _connection = _connection with { Connecting = false, Connected = false, Enhanced = false, Message = finalMessage };
                }
            }
            session.Release();
            if (notify) NotifyConnection();
        }
        if (fallback is not null) await RunAsync(fallback).ConfigureAwait(false);
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
    private void NotifyConnection()
    {
        ConnectionChanged?.Invoke();
        StatusChanged?.Invoke(Connection.Message);
    }
    public void Dispose()
    {
        SessionState? previous;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; previous = _session; _session = null;
            _connection = _connection with { Connecting = false, Connected = false, Generation = ++_generation };
        }
        previous?.Stop();
    }

    private sealed class SessionState(long generation, bool elevated)
    {
        internal readonly long Generation = generation;
        internal readonly bool Elevated = elevated;
        internal readonly CancellationTokenSource Cts = new();
        internal NamedPipeServerStream? Pipe;
        internal ITimer? Timer;
        internal bool HasFrame, ResumePending, ProgressAfterSuspend;
        internal Guid? CollectorRunId;
        internal long LastSequence = -1, LastProgressTimestamp, CheckFromTimestamp;
        internal void Stop()
        {
            // Final CTS disposal belongs to the reader, which may already have completed.
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
            Timer?.Dispose();
            Pipe?.Dispose();
        }
        internal void Release() { Timer?.Dispose(); Cts.Dispose(); }
    }
}
