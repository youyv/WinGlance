using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinGlance.Core;

namespace WinGlance.Statistics;

public sealed partial class StatisticsService
{
    private DateTimeOffset _nextCatalogCleanup = DateTimeOffset.MinValue;
    private void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        Execute("""
            CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, started INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sequences (collector TEXT PRIMARY KEY, sequence INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS coverage (entity TEXT NOT NULL, metric INTEGER NOT NULL, finish INTEGER NOT NULL, PRIMARY KEY(entity,metric));
            CREATE TABLE IF NOT EXISTS applications (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS buckets (
                session TEXT NOT NULL, day TEXT NOT NULL, kind INTEGER NOT NULL,
                start INTEGER NOT NULL, finish INTEGER NOT NULL, entity TEXT NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY(session,day,kind,start,entity));
            CREATE INDEX IF NOT EXISTS buckets_day ON buckets(kind,day,entity);
            CREATE INDEX IF NOT EXISTS buckets_session ON buckets(session,kind,entity);
            CREATE INDEX IF NOT EXISTS buckets_retention ON buckets(day,kind,session);
            """);
        using (var command = Command("INSERT OR IGNORE INTO sessions(id,started) VALUES($id,$started)"))
        { command.Parameters.AddWithValue("$id", _sessionId.ToString("N")); command.Parameters.AddWithValue("$started", _startedUtc.UtcTicks); command.ExecuteNonQuery(); }
        using (var command = Command("SELECT collector,sequence FROM sequences"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (Guid.TryParse(reader.GetString(0), out var id)) _sequences[id] = reader.GetInt64(1);
        using (var command = Command("SELECT json FROM applications"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) if (JsonSerializer.Deserialize(reader.GetString(0), StatisticsJsonContext.Default.ApplicationEntry) is { } item) _applications[item.AppId] = item;
        using (var command = Command("SELECT entity,metric,finish FROM coverage"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) _coverageEnds[(reader.GetString(0), (StatisticMetric)reader.GetInt32(1))] = reader.GetInt64(2);
        _lastRecorded = ReadDate("lastRecorded"); _lastSaved = ReadDate("lastSaved"); _lastReset = ReadDate("lastReset");
        _resetCutoff = ReadDate("resetCutoff") ?? DateTimeOffset.MinValue;
    }

    private void Flush()
    {
        if (_connection is null) return;
        var saved = _clock.GetUtcNow();
        using var transaction = _connection.BeginTransaction();
        using var bucketRead = Command("SELECT json FROM buckets WHERE session=$session AND day=$day AND kind=$kind AND start=$start AND entity=$entity", transaction);
        using var bucketWrite = Command("""
            INSERT INTO buckets(session,day,kind,start,finish,entity,json)
            VALUES($session,$day,$kind,$start,$finish,$entity,$json)
            ON CONFLICT(session,day,kind,start,entity) DO UPDATE SET finish=excluded.finish,json=excluded.json
            """, transaction);
        foreach (var item in _pending.Values) MergeStored(item, transaction, bucketRead, bucketWrite);
        using (var command = Command("INSERT INTO applications(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json", transaction))
        {
            var idParameter = command.Parameters.Add("$id", SqliteType.Text);
            var jsonParameter = command.Parameters.Add("$json", SqliteType.Text);
            command.Prepare();
            foreach (var id in _changedApplications)
            {
                idParameter.Value = id; jsonParameter.Value = JsonSerializer.Serialize(_applications[id], StatisticsJsonContext.Default.ApplicationEntry);
                command.ExecuteNonQuery();
            }
        }
        WriteSequences(transaction);
        using (var command = Command("INSERT INTO coverage(entity,metric,finish) VALUES($entity,$metric,$finish) ON CONFLICT(entity,metric) DO UPDATE SET finish=excluded.finish", transaction))
        {
            var entity = command.Parameters.Add("$entity", SqliteType.Text);
            var metric = command.Parameters.Add("$metric", SqliteType.Integer);
            var finish = command.Parameters.Add("$finish", SqliteType.Integer);
            command.Prepare();
            foreach (var (key, end) in _coverageEnds)
            { entity.Value = key.Entity; metric.Value = (int)key.Metric; finish.Value = end; command.ExecuteNonQuery(); }
        }
        SetMeta("lastRecorded", _lastRecorded?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "", transaction);
        SetMeta("lastSaved", saved.UtcTicks.ToString(CultureInfo.InvariantCulture), transaction);
        Compact(transaction, saved);
        transaction.Commit();
        _pending.Clear(); _changedApplications.Clear(); _lastSaved = saved; _storageWarning = null;
        _dayReadCache.Clear(); _queryCache.Clear();
        CleanupCatalog(saved);
    }

    private void MergeStored(StoredBucket item, SqliteTransaction transaction, SqliteCommand? sharedRead = null, SqliteCommand? sharedWrite = null)
    {
        var merged = new BucketData();
        using var ownedRead = sharedRead is null ? Command("SELECT json FROM buckets WHERE session=$session AND day=$day AND kind=$kind AND start=$start AND entity=$entity", transaction) : null;
        var read = sharedRead ?? ownedRead!;
        if (item.Key.Kind == 0 && _dayReadCache.TryGetValue(item.Key, out var cached)) merged.Merge(cached.Data);
        else
        {
            BindKey(read, item.Key);
            if (read.ExecuteScalar() is string json && JsonSerializer.Deserialize(json, StatisticsJsonContext.Default.BucketData) is { } previous) merged.Merge(previous);
        }
        merged.Merge(item.Data);
        using var ownedWrite = sharedWrite is null ? Command("""
            INSERT INTO buckets(session,day,kind,start,finish,entity,json)
            VALUES($session,$day,$kind,$start,$finish,$entity,$json)
            ON CONFLICT(session,day,kind,start,entity) DO UPDATE SET finish=excluded.finish,json=excluded.json
            """, transaction) : null;
        var write = sharedWrite ?? ownedWrite!;
        BindKey(write, item.Key); Parameter(write, "$finish", item.End);
        Parameter(write, "$json", JsonSerializer.Serialize(merged, StatisticsJsonContext.Default.BucketData)); write.ExecuteNonQuery();
    }

    private void Compact(SqliteTransaction transaction, DateTimeOffset now)
    {
        long hour = TimeSpan.TicksPerHour;
        long cutoff = now.AddHours(-24).UtcTicks / hour * hour;
        var old = new List<StoredBucket>();
        using (var read = Command("SELECT session,day,kind,start,finish,entity,json FROM buckets WHERE kind=300 AND finish<=$cutoff", transaction))
        {
            read.Parameters.AddWithValue("$cutoff", cutoff);
            using var reader = read.ExecuteReader();
            while (reader.Read()) old.Add(ReadBucket(reader));
        }
        foreach (var group in old.GroupBy(item => new BucketKey(item.Key.Session, item.Key.Day, 3600, item.Key.Start / hour * hour, item.Key.Entity)))
        {
            var total = new BucketData();
            foreach (var item in group) total.Merge(item.Data);
            MergeStored(new(group.Key, group.Key.Start + hour, total), transaction);
        }
        using (var delete = Command("DELETE FROM buckets WHERE kind=300 AND finish<=$cutoff", transaction))
        { delete.Parameters.AddWithValue("$cutoff", cutoff); delete.ExecuteNonQuery(); }
        string oldest = LocalDate(now).AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using (var delete = Command("DELETE FROM buckets WHERE day<$oldest AND (kind>0 OR session<>$session)", transaction))
        {
            delete.Parameters.AddWithValue("$oldest", oldest); delete.Parameters.AddWithValue("$session", _sessionId.ToString("N")); delete.ExecuteNonQuery();
        }
        // Catalog entries outlive processes, but not all retained data: avoid an unbounded list of historical host PIDs.
        if (now >= _nextCatalogCleanup)
        {
            using (var delete = Command("DELETE FROM applications WHERE id NOT IN (SELECT DISTINCT entity FROM buckets WHERE entity<>'@system')", transaction)) delete.ExecuteNonQuery();
            using (var delete = Command("DELETE FROM coverage WHERE entity NOT IN (SELECT id FROM applications)", transaction)) delete.ExecuteNonQuery();
        }
    }

    private void CleanupCatalog(DateTimeOffset now)
    {
        if (now < _nextCatalogCleanup) return;
        _nextCatalogCleanup = now.AddDays(1);
        var retained = new HashSet<string>();
        using (var command = Command("SELECT id FROM applications"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) retained.Add(reader.GetString(0));
        foreach (var id in _applications.Keys.Where(id => !retained.Contains(id) && !_running.Contains(id)).ToArray())
        {
            var icon = _applications[id].Software.IconPath;
            _applications.Remove(id); _iconChecks.Remove(id);
            foreach (var key in _coverageEnds.Keys.Where(key => key.Entity == id).ToArray()) _coverageEnds.Remove(key);
            if (icon is null) continue;
            var directory = Path.Combine(Path.GetDirectoryName(_databasePath)!, "StatisticsIcons") + Path.DirectorySeparatorChar;
            try
            {
                string full = Path.GetFullPath(icon);
                if (full.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }

    private StatisticsSnapshot Query(StatisticsRange range)
    {
        ValidateRange(range);
        var now = _clock.GetUtcNow();
        if (_queryCache.TryGetValue(range, out var cached) && LocalDate(cached.EndUtc) == LocalDate(now) &&
            string.Equals(cached.Warning, Warning, StringComparison.Ordinal)) return cached;
        var totals = ReadRange(range, true, null).GroupBy(item => item.Key.Entity)
            .ToDictionary(group => group.Key, group => Merge(group.Select(item => item.Data)));
        var system = totals.GetValueOrDefault(SystemEntity) ?? new();
        var apps = new List<ApplicationStatistics>();
        foreach (var (id, total) in totals)
        {
            if (id == SystemEntity || !_applications.TryGetValue(id, out var entry)) continue;
            apps.Add(new(id, entry.Software, total.ObservedSeconds, _running.Contains(id), entry.LastSeenUtc, total.SnapshotMetrics()));
        }
        var start = RangeStart(range, now);
        var result = new StatisticsSnapshot(range, start, now, _sessionId, new(system.ObservedSeconds, system.SnapshotMetrics()),
            apps.OrderByDescending(app => app.ObservedSeconds).ThenBy(app => app.Software.DisplayName).ToArray(),
            ReadTrend(range, SystemEntity), _lastRecorded, _lastReset, Warning, _lastSaved,
            !system.MemoryCapacityUnknown && system.MemoryCapacityMin == system.MemoryCapacityMax ? system.MemoryCapacityMin : null,
            !system.GpuCapacityUnknown && system.GpuCapacityMin == system.GpuCapacityMax ? system.GpuCapacityMin : null,
            string.Join(" / ", system.NetworkScopes.Order(StringComparer.Ordinal)), system.Quality());
        _queryCache[range] = result;
        return result;
    }

    private IReadOnlyList<StatisticsTrendPoint> ReadTrend(StatisticsRange range, string entity) =>
        ReadRange(range, false, entity).GroupBy(item => TrendWindow(item, entity))
            .OrderBy(group => group.Key.Start).Select(group =>
            {
                var total = Merge(group.Select(item => item.Data));
                return new StatisticsTrendPoint(new(group.Key.Start, TimeSpan.Zero), new(group.Key.End, TimeSpan.Zero),
                    total.ObservedSeconds, total.SnapshotMetrics());
            }).ToArray();

    private (long Start, long End) TrendWindow(StoredBucket item, string entity)
    {
        long hour = TimeSpan.TicksPerHour;
        long cutoff = _clock.GetUtcNow().AddHours(-24).UtcTicks / hour * hour;
        if (entity != SystemEntity && item.End <= cutoff)
        { long start = item.Key.Start / hour * hour; return (start, start + hour); }
        return (item.Key.Start, item.End);
    }

    private IReadOnlyList<StoredBucket> ReadRange(StatisticsRange range, bool days, string? entity)
    {
        var now = _clock.GetUtcNow();
        string firstDay = LocalDate(now).AddDays(range == StatisticsRange.LastSevenDays ? -6 : 0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string lastDay = LocalDate(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var items = new Dictionary<BucketKey, StoredBucket>();
        string where = (days ? "kind=0" : "kind>0") + " AND " +
            (range == StatisticsRange.CurrentSession ? "session=$session" : "day>=$first AND day<=$last") +
            (entity is null ? "" : " AND entity=$entity");
        using (var command = Command("SELECT session,day,kind,start,finish,entity,json FROM buckets WHERE " + where))
        {
            if (range == StatisticsRange.CurrentSession) command.Parameters.AddWithValue("$session", _sessionId.ToString("N"));
            else { command.Parameters.AddWithValue("$first", firstDay); command.Parameters.AddWithValue("$last", lastDay); }
            if (entity is not null) command.Parameters.AddWithValue("$entity", entity);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = new BucketKey(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetString(5));
                if (!days || !_dayReadCache.TryGetValue(key, out var item))
                {
                    item = new(key, reader.GetInt64(4), JsonSerializer.Deserialize(reader.GetString(6), StatisticsJsonContext.Default.BucketData) ?? new());
                    if (days && _dayReadCache.Count < 4096) _dayReadCache[key] = item;
                }
                items[item.Key] = item;
            }
        }
        foreach (var item in _pending.Values)
        {
            if ((item.Key.Kind == 0) != days || entity is not null && item.Key.Entity != entity ||
                !InRange(item.Key, range, firstDay, lastDay)) continue;
            var merged = new BucketData();
            if (items.TryGetValue(item.Key, out var existing)) merged.Merge(existing.Data);
            merged.Merge(item.Data);
            items[item.Key] = new(item.Key, item.End, merged);
        }
        return items.Values.ToArray();
    }

    private void Reset(StatisticsRange range)
    {
        ValidateRange(range);
        Flush();
        var now = _clock.GetUtcNow();
        string first = LocalDate(now).AddDays(range == StatisticsRange.LastSevenDays ? -6 : 0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string last = LocalDate(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var transaction = _connection!.BeginTransaction();
        using (var command = Command("DELETE FROM buckets WHERE " + (range == StatisticsRange.CurrentSession ? "session=$session" : "day>=$first AND day<=$last"), transaction))
        {
            if (range == StatisticsRange.CurrentSession) command.Parameters.AddWithValue("$session", _sessionId.ToString("N"));
            else { command.Parameters.AddWithValue("$first", first); command.Parameters.AddWithValue("$last", last); }
            command.ExecuteNonQuery();
        }
        WriteSequences(transaction);
        SetMeta("lastReset", now.UtcTicks.ToString(CultureInfo.InvariantCulture), transaction);
        SetMeta("resetCutoff", now.UtcTicks.ToString(CultureInfo.InvariantCulture), transaction);
        SetMeta("lastRecorded", "", transaction);
        using (var clear = Command("DELETE FROM coverage", transaction)) clear.ExecuteNonQuery();
        transaction.Commit();
        foreach (var key in _pending.Keys.Where(key => InRange(key, range, first, last)).ToArray()) _pending.Remove(key);
        _lastReset = now; _resetCutoff = now;
        _lastRecorded = null;
        _coverageEnds.Clear();
        _dayReadCache.Clear(); _queryCache.Clear();
    }

    private bool InRange(BucketKey key, StatisticsRange range, string first, string last) =>
        range == StatisticsRange.CurrentSession ? key.Session == _sessionId.ToString("N") :
            string.CompareOrdinal(key.Day, first) >= 0 && string.CompareOrdinal(key.Day, last) <= 0;

    private DateTime LocalDate(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _timeZone).Date;
    private DateTimeOffset RangeStart(StatisticsRange range, DateTimeOffset now)
    {
        if (range != StatisticsRange.CurrentSession) return DayStart(LocalDate(now).AddDays(range == StatisticsRange.LastSevenDays ? -6 : 0));
        using var command = Command("SELECT started FROM sessions WHERE id=$id");
        command.Parameters.AddWithValue("$id", _sessionId.ToString("N"));
        return command.ExecuteScalar() is long ticks ? new(ticks, TimeSpan.Zero) : _startedUtc;
    }

    private static void ValidateRange(StatisticsRange range)
    { if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range)); }
    private static BucketData Merge(IEnumerable<BucketData> data)
    {
        using var values = data.GetEnumerator();
        if (!values.MoveNext()) return new();
        var first = values.Current;
        if (!values.MoveNext()) return first;
        var result = new BucketData(); result.Merge(first);
        do { result.Merge(values.Current); } while (values.MoveNext());
        return result;
    }
    private static StoredBucket ReadBucket(SqliteDataReader reader) => new(
        new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetString(5)),
        reader.GetInt64(4), JsonSerializer.Deserialize(reader.GetString(6), StatisticsJsonContext.Default.BucketData) ?? new());

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    { var command = _connection!.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; return command; }
    private void Execute(string sql) { using var command = Command(sql); command.ExecuteNonQuery(); }
    private static void BindKey(SqliteCommand command, BucketKey key)
    {
        Parameter(command, "$session", key.Session); Parameter(command, "$day", key.Day);
        Parameter(command, "$kind", key.Kind); Parameter(command, "$start", key.Start);
        Parameter(command, "$entity", key.Entity);
    }
    private static void Parameter(SqliteCommand command, string name, object value)
    { if (command.Parameters.Contains(name)) command.Parameters[name].Value = value; else command.Parameters.AddWithValue(name, value); }
    private void WriteSequences(SqliteTransaction transaction)
    {
        foreach (var (collector, sequence) in _sequences)
        {
            using var command = Command("INSERT INTO sequences(collector,sequence) VALUES($id,$sequence) ON CONFLICT(collector) DO UPDATE SET sequence=excluded.sequence", transaction);
            command.Parameters.AddWithValue("$id", collector.ToString("N")); command.Parameters.AddWithValue("$sequence", sequence); command.ExecuteNonQuery();
        }
    }
    private void SetMeta(string key, string value, SqliteTransaction transaction)
    {
        using var command = Command("INSERT INTO meta(name,value) VALUES($name,$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value", transaction);
        command.Parameters.AddWithValue("$name", key); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
    }
    private DateTimeOffset? ReadDate(string key)
    {
        using var command = Command("SELECT value FROM meta WHERE name=$name"); command.Parameters.AddWithValue("$name", key);
        return long.TryParse(command.ExecuteScalar() as string, out var ticks) ? new(ticks, TimeSpan.Zero) : null;
    }
}
