using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using WinGlance.Core;

namespace WinGlance.Statistics;

/// <summary>Owns a single background database writer. Accept never performs disk I/O or waits for the UI.</summary>
public sealed partial class StatisticsService : IAsyncDisposable
{
    private const string SystemEntity = "@system";
    private readonly string _databasePath;
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeProvider _clock;
    private readonly Guid _sessionId;
    private readonly string _sessionKey;
    private readonly DateTimeOffset _startedUtc;
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly CancellationTokenSource _timerStop = new();
    private readonly Task _worker, _timer;
    private readonly Dictionary<BucketKey, StoredBucket> _pending = [];
    private readonly Dictionary<BucketKey, StoredBucket> _storedReadCache = [];
    private const int StoredReadCacheLimit = 4096;
    private readonly Dictionary<StatisticsRange, StatisticsSnapshot> _queryCache = [];
    private readonly Dictionary<(string Key, string? Path), string> _stableIds = [];
    private readonly Dictionary<(Guid Collector, ProcessIdentity Process), string> _instanceIds = [];
    private readonly Dictionary<(int Width, long Start, long End), DistributionPiece[]> _distributionCache = [];
    private readonly Dictionary<string, ApplicationEntry> _applications = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedApplications = [];
    private readonly Dictionary<string, (string? Source, DateTimeOffset Checked)> _iconChecks = [];
    private readonly Dictionary<Guid, long> _sequences = [];
    private readonly Dictionary<(string Entity, StatisticMetric Metric), long> _coverageEnds = [];
    private readonly HashSet<(string Entity, StatisticMetric Metric)> _dirtyCoverage = [];
    private HashSet<string> _running = [];
    private SqliteConnection? _connection;
    private DateTimeOffset? _lastRecorded, _lastSaved, _lastReset;
    private DateTimeOffset _resetCutoff = DateTimeOffset.MinValue;
    private string? _warning, _storageWarning;
    private long? _memoryCapacity, _gpuCapacity;
    private string? _networkScope;
    private int _accepting = 1, _queuedSamples;
    private Exception? _fatalError;
    private static long _nextDiagnosticId;
    // Used only by the single writer; lets a reset's prerequisite flush share its request ID.
    private long _activeDiagnosticId;

    public StatisticsService(string databasePath, TimeZoneInfo? timeZone = null, Guid? sessionId = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _clock = timeProvider ?? TimeProvider.System;
        _sessionId = sessionId ?? Guid.NewGuid();
        _sessionKey = _sessionId.ToString("N");
        _startedUtc = _clock.GetUtcNow();
        _worker = Task.Run(RunAsync);
        _timer = Task.Run(TimerAsync);
    }

    public Guid SessionId => _sessionId;

    public bool Accept(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Accounting is null || Volatile.Read(ref _accepting) == 0 || _fatalError is not null) return false;
        if (Interlocked.Increment(ref _queuedSamples) > 60)
        { Interlocked.Decrement(ref _queuedSamples); Volatile.Write(ref _warning, "统计队列已满，部分采集区间未记录。"); return false; }
        var work = new SampleWork(snapshot);
        // Admission may run inside the collector lifecycle gate. Only capture the timestamp
        // here; emit its queue latency from the worker, never invoke diagnostic listeners here.
        if (_queue.Writer.TryWrite(work)) return true;
        Interlocked.Decrement(ref _queuedSamples);
        return false;
    }

    public Task<StatisticsSnapshot> QueryAsync(StatisticsRange range, CancellationToken cancellationToken = default) =>
        Enqueue(() => Query(range), cancellationToken, "query");

    public Task<ApplicationStatisticsDetail> QueryApplicationAsync(StatisticsRange range, string appId,
        CancellationToken cancellationToken = default) => Enqueue(() =>
        {
            var context = CreateQueryContext(range, cancellationToken);
            var snapshot = Query(context, useCache: false);
            return new ApplicationStatisticsDetail(snapshot.Applications.FirstOrDefault(app => app.AppId == appId),
                ReadTrend(context, appId));
        }, cancellationToken, "queryApplication");

    public Task ResetAsync(StatisticsRange range, CancellationToken cancellationToken = default) =>
        Enqueue(() => { Reset(range); return true; }, cancellationToken, "reset", "reset");

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        Enqueue(() => { Flush("manual", _activeDiagnosticId); return true; }, cancellationToken, "flush", "manual");

    private Task<T> Enqueue<T>(Func<T> action, CancellationToken cancellationToken, string operation, string? flushReason = null)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        if (_fatalError is { } error) return Task.FromException<T>(new IOException("统计存储不可用。", error));
        if (Volatile.Read(ref _accepting) == 0) return Task.FromException<T>(new ObjectDisposedException(nameof(StatisticsService)));
        var work = new RequestWork<T>(action, operation, flushReason, cancellationToken);
        work.Queued();
        if (!_queue.Writer.TryWrite(work))
        { work.Rejected(); return Task.FromException<T>(new ObjectDisposedException(nameof(StatisticsService))); }
        return work.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task TimerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_timerStop.Token).ConfigureAwait(false))
            {
                var work = new FlushWork();
                work.Queued();
                if (!_queue.Writer.TryWrite(work)) { work.Rejected(); break; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            Initialize();
            await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                _activeDiagnosticId = work.DiagnosticId;
                long executionStarted = work.Started();
                try
                {
                    switch (work)
                    {
                        case SampleWork sample:
                            Interlocked.Decrement(ref _queuedSamples);
                            Process(sample.Snapshot);
                            break;
                        case FlushWork: Flush("periodic", work.DiagnosticId); break;
                        default: work.Execute(); break;
                    }
                    work.Finished(executionStarted);
                }
                catch (OperationCanceledException) when (work.IsCancellationRequested)
                {
                    work.Cancel();
                }
                catch (Exception ex)
                {
                    if (work is SampleWork failedSample)
                        _warning = $"{LocalWarningTime(failedSample.Snapshot.Accounting!.EndUtc)} 的统计帧未记录：{ex.Message}";
                    else _storageWarning = $"统计暂未完整保存：{ex.Message}";
                    work.Failed(ex, executionStarted);
                    work.Fail(ex);
                }
            }
            Flush("exit");
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            _queue.Writer.TryComplete(ex);
            while (_queue.Reader.TryRead(out var remaining))
            { remaining.Failed(ex, 0); remaining.Fail(ex); }
        }
        finally { _connection?.Dispose(); }
    }

    private void Process(MonitorSnapshot snapshot)
    {
        var frame = snapshot.Accounting!;
        if (frame.Sequence < 0 || _sequences.TryGetValue(frame.CollectorRunId, out var previous) && frame.Sequence <= previous) return;
        _queryCache.Clear(); _distributionCache.Clear();
        _sequences[frame.CollectorRunId] = frame.Sequence;
        var running = snapshot.Processes.Select(p => AppId(p.Software, p.Identity, frame.CollectorRunId)).ToHashSet();
        if (_running.Any(id => !running.Contains(id))) _catalogNeedsCleanup = true;
        _running = running;
        var groups = frame.Processes.GroupBy(p => AppId(p.Software, p.Identity, frame.CollectorRunId)).ToArray();
        foreach (var group in groups)
            Remember(group.Key, group.OrderByDescending(p => p.Software.IconPath is not null)
                .ThenByDescending(p => p.Software.DisplayName != p.Software.ProcessName).First(), frame);
        if (frame.System is { } system)
        {
            _memoryCapacity = system.TotalMemoryBytes;
            _gpuCapacity = system.DedicatedGpuCapacityBytes;
            _networkScope = system.NetworkScope ?? _networkScope;
        }
        if (frame.Discontinuity)
        {
            if (frame.DiscontinuityReason?.StartsWith("首次采样", StringComparison.Ordinal) != true)
                _warning = $"{LocalWarningTime(frame.EndUtc)} 发生采集中断，该时段未计入统计：{frame.DiscontinuityReason ?? "采集区间不连续"}";
            return;
        }
        if (!double.IsFinite(frame.IntervalSeconds) || frame.IntervalSeconds <= 0 || frame.EndUtc <= frame.StartUtc ||
            frame.StartUtc < _resetCutoff) return;
        AddObserved(SystemEntity, frame.StartUtc, frame.EndUtc, frame.IntervalSeconds, frame.System?.EtwQuality);
        if (frame.System is not null)
            foreach (var (metric, value) in frame.System.Metrics) AddMetric(SystemEntity, metric, value, frame);
        foreach (var group in groups)
        {
            var members = group.GroupBy(p => p.Identity).Select(g => g.Last()).ToArray();
            foreach (var observed in ProcessAccountingAggregation.ObservedWindows(members, frame))
                AddObserved(group.Key, observed.Start, observed.End, (observed.End - observed.Start).TotalSeconds, null);
            foreach (var metric in StatisticMetrics.All)
            {
                // Preserve exact measured counters even if another child is unavailable in the same interval.
                foreach (var member in members)
                {
                    if (!member.Metrics.TryGetValue(metric, out var raw) ||
                        raw.BytesDelta is null && raw.CpuTicksDelta is null) continue;
                    if (!TryWindow(raw, frame, out var start, out var end)) continue;
                    Distribute(group.Key, start, end, (data, from, to, total, pieceEnd) =>
                        data.Metric(metric).AddRaw(Portion(raw.BytesDelta, from, to, total), Portion(raw.CpuTicksDelta, from, to, total)));
                }
                // Segment boundaries preserve disjoint lifetimes and avoid adding peaks measured at different times.
                var segments = ProcessAccountingAggregation.AggregateMetric(members, metric, frame);
                var coverageKey = (group.Key, metric);
                long coveredUntil = _coverageEnds.GetValueOrDefault(coverageKey);
                long previousCoverage = coveredUntil;
                foreach (var value in segments)
                {
                    if (!TryWindow(value, frame, out var start, out var end) || end.UtcTicks <= coveredUntil) continue;
                    var clipped = value;
                    if (start.UtcTicks < coveredUntil)
                    {
                        double fraction = (double)(end.UtcTicks - coveredUntil) / (end.UtcTicks - start.UtcTicks);
                        start = new(coveredUntil, TimeSpan.Zero);
                        clipped = value with { StartUtc = start, Integral = value.Integral * fraction,
                            ValidSeconds = value.ValidSeconds * fraction, IntervalSeconds = (end - start).TotalSeconds };
                    }
                    AddMetric(group.Key, metric, clipped, frame);
                    coveredUntil = end.UtcTicks;
                }
                if (coveredUntil != previousCoverage)
                {
                    _coverageEnds[coverageKey] = coveredUntil;
                    _dirtyCoverage.Add(coverageKey);
                }
            }
        }
        _lastRecorded = frame.EndUtc;
    }

    private void AddObserved(string entity, DateTimeOffset start, DateTimeOffset end, double seconds, EtwQualityAccounting? quality)
    {
        if (seconds <= 0 || !double.IsFinite(seconds)) return;
        Distribute(entity, start, end, (data, from, to, total, pieceEnd) =>
        {
            data.ObservedSeconds += seconds * (to - from) / total;
            if (entity == SystemEntity) data.Capacities(_memoryCapacity, _gpuCapacity, _networkScope);
            if (quality is null) return;
            data.UnattributedDownload += Portion(quality.UnattributedDownloadBytes, from, to, total) ?? 0;
            data.UnattributedUpload += Portion(quality.UnattributedUploadBytes, from, to, total) ?? 0;
            data.UnattributedRead += Portion(quality.UnattributedDiskReadBytes, from, to, total) ?? 0;
            data.UnattributedWrite += Portion(quality.UnattributedDiskWriteBytes, from, to, total) ?? 0;
            data.UnattributedEvents += Portion(quality.UnattributedEvents, from, to, total) ?? 0;
            data.LostEvents += Portion(quality.LostEvents, from, to, total) ?? 0;
            data.EtwSeconds += quality.IntervalSeconds * (to - from) / total;
            data.EtwState = quality.State;
        });
    }

    private bool TryWindow(MetricAccounting value, AccountingFrame frame, out DateTimeOffset start, out DateTimeOffset end)
    {
        bool valid = ProcessAccountingAggregation.TryMetricWindow(value, frame, out var window);
        start = window.Start; end = window.End;
        return valid && start >= _resetCutoff;
    }

    private void AddMetric(string entity, StatisticMetric metric, MetricAccounting value, AccountingFrame frame,
        bool includeCoverage = true, bool includePeak = true)
    {
        if (value.State != MetricState.Ready || value.Integral is null || value.ValidSeconds <= 0 ||
            !double.IsFinite(value.ValidSeconds) || !double.IsFinite(value.Integral.Value) || value.Integral < 0) return;
        if (!TryWindow(value, frame, out var start, out var end)) return;
        var peakTime = value.PeakUtc ?? end;
        if (peakTime < start || peakTime > end) peakTime = end;
        Distribute(entity, start, end, (data, from, to, total, pieceEnd) =>
            data.Metric(metric).Add(value, (double)(to - from) / total, pieceEnd,
                Portion(value.BytesDelta, from, to, total), Portion(value.CpuTicksDelta, from, to, total), includeCoverage, false));
        if (includePeak && value.Peak is { } peak && double.IsFinite(peak) && peak >= 0)
            Distribute(entity, peakTime.AddTicks(-1), peakTime, (data, from, to, total, pieceEnd) =>
            {
                var target = data.Metric(metric);
                if (target.Peak is null || peak > target.Peak) { target.Peak = peak; target.PeakUtc = peakTime; }
            });
    }

    private void Distribute(string entity, DateTimeOffset start, DateTimeOffset end,
        Action<BucketData, long, long, long, DateTimeOffset> add)
    {
        long first = start.UtcTicks, last = end.UtcTicks, total = last - first;
        if (total <= 0) return;
        int width = entity == SystemEntity ? 60 : 300;
        if (!_distributionCache.TryGetValue((width, first, last), out var pieces))
            _distributionCache[(width, first, last)] = pieces = DistributionPieces(width, first, last);
        foreach (var piece in pieces)
        {
            var dayKey = new BucketKey(_sessionKey, piece.Day, 0, piece.DayStart, entity);
            var bucketKey = new BucketKey(_sessionKey, piece.Day, width, piece.BucketStart, entity);
            add(Pending(dayKey, piece.DayEnd), piece.From, piece.To, total, piece.End);
            add(Pending(bucketKey, piece.BucketEnd), piece.From, piece.To, total, piece.End);
        }
    }

    private DistributionPiece[] DistributionPieces(int width, long first, long last)
    {
        var result = new List<DistributionPiece>();
        long widthTicks = TimeSpan.FromSeconds(width).Ticks;
        for (long cursor = first; cursor < last;)
        {
            var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(cursor, TimeSpan.Zero), _timeZone);
            var day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dayStart = DayStart(local.Date).UtcTicks;
            var dayEnd = DayStart(local.Date.AddDays(1)).UtcTicks;
            long bucketStart = cursor / widthTicks * widthTicks;
            long next = Math.Min(last, Math.Min(dayEnd, bucketStart + widthTicks));
            if (next <= cursor) throw new InvalidOperationException("统计时间区间无效。");
            result.Add(new(day, dayStart, dayEnd, Math.Max(bucketStart, dayStart), Math.Min(bucketStart + widthTicks, dayEnd),
                cursor - first, next - first, new(next, TimeSpan.Zero)));
            cursor = next;
        }
        return result.ToArray();
    }

    private readonly record struct DistributionPiece(string Day, long DayStart, long DayEnd,
        long BucketStart, long BucketEnd, long From, long To, DateTimeOffset End);

    private BucketData Pending(BucketKey key, long end)
    {
        if (!_pending.TryGetValue(key, out var item)) _pending[key] = item = new(key, end, new());
        return item.Data;
    }

    private static long? Portion(long? value, long from, long to, long total)
    {
        if (value is null || value < 0) return null;
        return checked((long)(decimal.Floor((decimal)value.Value * to / total) - decimal.Floor((decimal)value.Value * from / total)));
    }

    private DateTimeOffset DayStart(DateTime day)
    {
        var local = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
        while (_timeZone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = _timeZone.IsAmbiguousTime(local) ? _timeZone.GetAmbiguousTimeOffsets(local).Max() : _timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private string LocalWarningTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, _timeZone).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private string? Warning => _warning is null ? _storageWarning : _storageWarning is null ? _warning : _warning + "；" + _storageWarning;

    private string AppId(SoftwareIdentity software, ProcessIdentity process, Guid collector)
    {
        bool stable = !software.Key.StartsWith("process:", StringComparison.OrdinalIgnoreCase) &&
            (software.Key.StartsWith("package:", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(software.FilePath));
        var cacheKey = (software.Key, software.FilePath);
        if (stable && _stableIds.TryGetValue(cacheKey, out var cached)) return cached;
        var instanceKey = (collector, process);
        if (!stable && _instanceIds.TryGetValue(instanceKey, out cached)) return cached;
        string identity;
        if (software.Key.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
        {
            int separator = software.Key.IndexOf(':', "package:".Length);
            identity = separator >= 0 ? "package:" + software.Key[(separator + 1)..] : software.Key;
        }
        else if (software.Key.StartsWith("process:", StringComparison.OrdinalIgnoreCase))
            identity = $"instance:{collector:N}:{process.Pid}:{process.StartTimeUtcTicks}";
        else if (!string.IsNullOrWhiteSpace(software.FilePath))
            identity = "file:" + software.FilePath.Trim().Replace('/', '\\').TrimEnd('\\');
        else identity = $"instance:{collector:N}:{process.Pid}:{process.StartTimeUtcTicks}";
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
        if (stable)
        {
            if (_stableIds.Count >= 4096) _stableIds.Clear();
            _stableIds[cacheKey] = id;
        }
        else
        {
            if (_instanceIds.Count >= 4096) _instanceIds.Clear();
            _instanceIds[instanceKey] = id;
        }
        return id;
    }

    private void Remember(string id, ProcessAccounting process, AccountingFrame frame)
    {
        _applications.TryGetValue(id, out var previous);
        var software = process.Software;
        bool usefulName = !string.Equals(software.DisplayName, software.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(software.DisplayName + ".exe", software.ProcessName, StringComparison.OrdinalIgnoreCase);
        var icon = previous?.Software.IconPath;
        bool checkIcon = !_iconChecks.TryGetValue(id, out var checkedIcon) || checkedIcon.Source != software.IconPath ||
            frame.EndUtc - checkedIcon.Checked > TimeSpan.FromMinutes(10);
        if (checkIcon && software.IconPath is { } source)
        {
            _iconChecks[id] = (source, frame.EndUtc);
            try
            {
                if (!File.Exists(source)) throw new FileNotFoundException();
                var directory = Path.Combine(Path.GetDirectoryName(_databasePath)!, "StatisticsIcons");
                Directory.CreateDirectory(directory);
                var destination = Path.Combine(directory, id + ".png");
                if (!File.Exists(destination) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(destination)) File.Copy(source, destination, true);
                icon = destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        software = software with { Key = id, IconPath = icon,
            DisplayName = usefulName || previous is null ? software.DisplayName : previous.Software.DisplayName };
        _applications[id] = new(id, software, previous?.FirstSeenUtc ?? frame.StartUtc, frame.EndUtc);
        _changedApplications.Add(id);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0) { await _worker.ConfigureAwait(false); return; }
        _timerStop.Cancel();
        await _timer.ConfigureAwait(false);
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        _timerStop.Dispose();
        if (_fatalError is { } error) throw new IOException("统计数据未能完整保存。", error);
    }

    private abstract record Work
    {
        protected Work(string operation, string? flushReason = null)
        {
            Operation = operation; FlushReason = flushReason;
            _queuedAt = DiagnosticTrace.Start();
            DiagnosticId = _queuedAt == 0 ? 0 : Interlocked.Increment(ref _nextDiagnosticId);
        }
        private readonly long _queuedAt;
        private string Operation { get; }
        private string? FlushReason { get; }
        public long DiagnosticId { get; }
        public void Queued()
        {
            if (_queuedAt == 0) return;
            DiagnosticTrace.Log.StatisticsWork("queued", Operation, DiagnosticId, 0, 0, "");
            if (FlushReason is { } reason) DiagnosticTrace.Log.Flush("queued", reason, DiagnosticId, 0);
        }
        public long Started()
        {
            if (_queuedAt == 0) return 0;
            long started = Stopwatch.GetTimestamp();
            DiagnosticTrace.Log.StatisticsWork("start", Operation, DiagnosticId,
                Stopwatch.GetElapsedTime(_queuedAt, started).TotalMilliseconds, 0, "");
            return started;
        }
        public void Finished(long started)
        {
            if (started != 0) DiagnosticTrace.Log.StatisticsWork("finished", Operation, DiagnosticId,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, this is SampleWork sample ? sample.Snapshot.Processes.Count : 0, "");
        }
        public void Failed(Exception exception, long started)
        {
            if (_queuedAt != 0) DiagnosticTrace.Log.StatisticsWork("failed", Operation, DiagnosticId,
                started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, exception.GetType().Name);
        }
        public void Rejected()
        {
            if (_queuedAt != 0) DiagnosticTrace.Log.StatisticsWork("rejected", Operation, DiagnosticId, 0, 0, "queueClosed");
        }
        public virtual void Execute() { }
        public virtual void Fail(Exception exception) { }
        public virtual bool IsCancellationRequested => false;
        public virtual void Cancel() { }
    }
    private sealed record SampleWork(MonitorSnapshot Snapshot) : Work("sample");
    private sealed record FlushWork() : Work("flush", "periodic");
    private sealed record RequestWork<T>(Func<T> Action, string Name, string? Reason, CancellationToken Token) : Work(Name, Reason)
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool IsCancellationRequested => Token.IsCancellationRequested;
        public override void Execute()
        {
            Token.ThrowIfCancellationRequested();
            Completion.TrySetResult(Action());
        }
        public override void Cancel() => Completion.TrySetCanceled(Token);
        public override void Fail(Exception exception) => Completion.TrySetException(exception);
    }
}
