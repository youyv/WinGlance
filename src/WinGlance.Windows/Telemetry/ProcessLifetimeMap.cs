using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

/// <summary>
/// Bounded event-time attribution. PID reuse is a new lifetime, never an overwrite of an old one.
/// Callers serialize access. Unverifiable generations form a fence and intentionally resolve to null.
/// </summary>
internal sealed class ProcessLifetimeMap
{
    private readonly Dictionary<int, List<Lifetime>> _lifetimes = [];
    private const int MaximumPids = 32768;
    private const int MaximumGenerationsPerPid = 4;
    private static readonly long RetentionTicks = TimeSpan.FromSeconds(15).Ticks;

    public void ObserveSnapshot(IEnumerable<ProcessIdentity> identities, long nowUtcTicks)
    {
        foreach (var identity in identities)
        {
            if (identity.StartTimeUtcTicks <= 0) continue;
            var list = GetOrCreate(identity.Pid);
            if (list is null) continue;
            var existing = list.FirstOrDefault(x => x.Identity == identity);
            if (existing is not null) { existing.LastObserved = nowUtcTicks; continue; }
            // A delayed snapshot cannot resurrect an already stopped, known lifetime.
            var unknown = list.LastOrDefault(x => x.Identity is null && x.Start >= identity.StartTimeUtcTicks);
            long lastKnownStart = list.Where(x => x.Identity is not null).Select(x => x.Start).DefaultIfEmpty(0).Max();
            if (unknown is not null && identity.StartTimeUtcTicks > lastKnownStart)
            {
                unknown.Identity = identity;
                unknown.Start = identity.StartTimeUtcTicks;
                unknown.LastObserved = nowUtcTicks;
                Normalize(list);
            }
            else Insert(list, new Lifetime(identity.StartTimeUtcTicks, identity, 0) { LastObserved = nowUtcTicks });
        }
        long cutoff = nowUtcTicks - RetentionTicks;
        foreach (var pid in _lifetimes.Keys.ToArray())
        {
            _lifetimes[pid].RemoveAll(x => x.End is { } end ? end < cutoff : x.LastObserved < cutoff);
            if (_lifetimes[pid].Count == 0) _lifetimes.Remove(pid);
        }
    }

    public void ObserveStart(int pid, long eventUtcTicks, ulong processKey, ProcessIdentity? identity)
    {
        var list = GetOrCreate(pid);
        if (list is null) return;
        // Opening a PID after receiving a delayed start event may open its replacement process.
        if (identity is { } known && (known.Pid != pid || known.StartTimeUtcTicks > eventUtcTicks || known.StartTimeUtcTicks <= 0)) identity = null;
        var existing = identity is null ? null : list.FirstOrDefault(x => x.Identity == identity);
        if (existing is not null) { existing.ProcessKey = processKey; existing.LastObserved = eventUtcTicks; return; }
        Insert(list, new Lifetime(identity?.StartTimeUtcTicks ?? eventUtcTicks, identity, processKey) { LastObserved = eventUtcTicks });
    }

    public ProcessIdentity? ObserveStop(int pid, long eventUtcTicks, ulong processKey)
    {
        if (!_lifetimes.TryGetValue(pid, out var list)) return null;
        // Match the kernel process key before considering a snapshot-only generation.
        var lifetime = processKey == 0 ? null : list.LastOrDefault(x => x.ProcessKey == processKey && x.Start <= eventUtcTicks);
        lifetime ??= list.LastOrDefault(x => (x.ProcessKey == 0 || processKey == 0) && x.Start <= eventUtcTicks);
        if (lifetime is not null) lifetime.End = Math.Min(lifetime.End ?? long.MaxValue, eventUtcTicks);
        return lifetime?.Identity;
    }

    public ProcessIdentity? Resolve(int pid, long eventUtcTicks)
    {
        if (!_lifetimes.TryGetValue(pid, out var list)) return null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var lifetime = list[i];
            if (eventUtcTicks >= lifetime.Start)
                return lifetime.End is { } end && eventUtcTicks >= end ? null : lifetime.Identity;
        }
        return null;
    }

    public void Invalidate()
    {
        // A dropped process start/stop event can remove a reuse boundary; rebuild from snapshots.
        _lifetimes.Clear();
    }

    private List<Lifetime>? GetOrCreate(int pid)
    {
        if (pid <= 0) return null;
        if (_lifetimes.TryGetValue(pid, out var list)) return list;
        if (_lifetimes.Count >= MaximumPids) return null;
        return _lifetimes[pid] = [];
    }

    private static void Insert(List<Lifetime> list, Lifetime lifetime)
    {
        // Duplicate rundown/start delivery must not create an overlapping generation.
        var same = list.FirstOrDefault(x => x.Start == lifetime.Start);
        if (same is not null)
        {
            same.Identity ??= lifetime.Identity;
            if (lifetime.ProcessKey != 0) same.ProcessKey = lifetime.ProcessKey;
        }
        else list.Add(lifetime);
        Normalize(list);
        if (list.Count > MaximumGenerationsPerPid) list.RemoveRange(0, list.Count - MaximumGenerationsPerPid);
    }

    private static void Normalize(List<Lifetime> list)
    {
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i + 1 < list.Count; i++)
            list[i].End = Math.Min(list[i].End ?? long.MaxValue, list[i + 1].Start);
    }

    private sealed class Lifetime(long start, ProcessIdentity? identity, ulong processKey)
    {
        public long Start = start;
        public long? End;
        public ProcessIdentity? Identity = identity;
        public ulong ProcessKey = processKey;
        public long LastObserved = start;
    }
}
