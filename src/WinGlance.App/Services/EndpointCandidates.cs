using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinGlance.Core;

namespace WinGlance.App.Services;

internal enum EndpointProtocol : byte { Tcp4, Tcp6, Udp4, Udp6 }

// Network addresses are canonical big-endian numbers, not strings or native-endian DWORDs.
// No per-row byte arrays, IPAddress objects, process references, or strings are retained.
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct EndpointCandidate(
    ulong LocalHigh, ulong LocalLow, ulong RemoteHigh, ulong RemoteLow,
    uint LocalScope, uint RemoteScope, int Pid, uint State,
    ushort LocalPort, ushort RemotePort, EndpointProtocol Protocol) : IComparable<EndpointCandidate>
{
    internal bool HasRemote => Protocol is EndpointProtocol.Tcp4 or EndpointProtocol.Tcp6 && State != 2;
    internal bool IsIPv6 => Protocol is EndpointProtocol.Tcp6 or EndpointProtocol.Udp6;

    public int CompareTo(EndpointCandidate other)
    {
        int order = ((byte)Protocol).CompareTo((byte)other.Protocol);
        if (order == 0) order = Pid.CompareTo(other.Pid);
        if (order == 0) order = LocalHigh.CompareTo(other.LocalHigh);
        if (order == 0) order = LocalLow.CompareTo(other.LocalLow);
        if (order == 0) order = LocalScope.CompareTo(other.LocalScope);
        if (order == 0) order = LocalPort.CompareTo(other.LocalPort);
        if (order == 0) order = HasRemote.CompareTo(other.HasRemote);
        if (order == 0 && HasRemote) order = RemoteHigh.CompareTo(other.RemoteHigh);
        if (order == 0 && HasRemote) order = RemoteLow.CompareTo(other.RemoteLow);
        if (order == 0 && HasRemote) order = RemoteScope.CompareTo(other.RemoteScope);
        if (order == 0 && HasRemote) order = RemotePort.CompareTo(other.RemotePort);
        if (order == 0) order = State.CompareTo(other.State);
        return order;
    }
}

// This accounts for explicitly owned active buffers, not GC committed bytes, OS handles,
// runtime allocations, or process working set. No global pool retains these query buffers.
internal sealed class EndpointWorkBudget
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    // Bounded PID/identity dictionaries, selectors, final 200 DTOs and formatting scratch.
    internal const int MetadataAndOutputReserve = 384 * 1024;
    internal long Limit { get; }
    internal long CurrentBytes { get; private set; }
    internal long HighWaterBytes { get; private set; }

    internal EndpointWorkBudget(long limit = MaximumBytes)
    {
        if (limit < MetadataAndOutputReserve || limit > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(limit));
        Limit = limit;
        CurrentBytes = HighWaterBytes = MetadataAndOutputReserve;
    }

    internal bool TryReserve(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes > Limit - CurrentBytes) return false;
        CurrentBytes += bytes;
        HighWaterBytes = Math.Max(HighWaterBytes, CurrentBytes);
        return true;
    }

    internal void Release(long bytes)
    {
        if (bytes < 0 || bytes > CurrentBytes - MetadataAndOutputReserve)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        CurrentBytes -= bytes;
    }
}

// A fixed-capacity max heap keeps the lowest K values without expanding an array or
// allocating comparer closures. Sorting is restricted to the final global <=200 entries.
internal sealed class EndpointTopK
{
    private readonly EndpointCandidate[] _items;
    internal int Count { get; private set; }
    internal ReadOnlySpan<EndpointCandidate> Items => _items.AsSpan(0, Count);
    internal EndpointTopK(int capacity) => _items = new EndpointCandidate[capacity];

    internal void Add(EndpointCandidate item)
    {
        if (Count < _items.Length)
        {
            int index = Count++;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (_items[parent].CompareTo(item) >= 0) break;
                _items[index] = _items[parent]; index = parent;
            }
            _items[index] = item;
        }
        else if (_items.Length > 0 && item.CompareTo(_items[0]) < 0)
        {
            int index = 0;
            while (index * 2 + 1 < Count)
            {
                int child = index * 2 + 1;
                if (child + 1 < Count && _items[child + 1].CompareTo(_items[child]) > 0) child++;
                if (_items[child].CompareTo(item) <= 0) break;
                _items[index] = _items[child]; index = child;
            }
            _items[index] = item;
        }
    }

    internal EndpointCandidate[] SortedCopy()
    {
        var result = Items.ToArray();
        Array.Sort(result);
        return result;
    }
}

internal sealed record EndpointSelectionResult(EndpointCandidate[] Candidates, int MatchedCount,
    bool DisplayTruncated, bool CandidateBudgetExceeded);

internal sealed class EndpointCandidateSet : IDisposable
{
    internal static int CandidateBytes => Unsafe.SizeOf<EndpointCandidate>();
    // Conservative array header/alignment and one heap object allowance per process.
    internal static int ProcessBlockBytes => checked(CandidateBytes * IpHelperConnections.MaximumConnections + 96);
    private readonly EndpointWorkBudget _budget;
    private readonly Dictionary<int, Bucket> _processes = new(IpHelperConnections.MaximumProcesses);
    private long _reserved;
    private bool _disposed;
    private sealed class Bucket { internal EndpointTopK? Heap; internal int Matched; }
    internal bool CandidateBudgetExceeded { get; private set; }
    internal int RetainedCount { get; private set; }
    internal void MarkBudgetExceeded() => CandidateBudgetExceeded = true;

    internal EndpointCandidateSet(EndpointWorkBudget budget)
    {
        if (CandidateBytes > 64 || RuntimeHelpers.IsReferenceOrContainsReferences<EndpointCandidate>())
            throw new InvalidOperationException("Endpoint candidates must be reference-free and at most 64 bytes.");
        _budget = budget;
    }

    internal bool AddProcess(int pid)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_processes.ContainsKey(pid)) return true;
        if (_processes.Count >= IpHelperConnections.MaximumProcesses) return false;
        _processes.Add(pid, new());
        return true;
    }

    internal void Add(EndpointCandidate candidate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_processes.TryGetValue(candidate.Pid, out var bucket))
            throw new InvalidOperationException("Endpoint has no verified process slot.");
        bucket.Matched = checked(bucket.Matched + 1);
        if (bucket.Heap is null)
        {
            if (!_budget.TryReserve(ProcessBlockBytes)) { CandidateBudgetExceeded = true; return; }
            _reserved += ProcessBlockBytes;
            bucket.Heap = new(IpHelperConnections.MaximumConnections);
        }
        int before = bucket.Heap.Count;
        bucket.Heap.Add(candidate);
        RetainedCount += bucket.Heap.Count - before;
    }

    internal EndpointSelectionResult Select(ISet<int> alive, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        var global = new EndpointTopK(IpHelperConnections.MaximumConnections);
        int matched = 0, visited = 0;
        foreach (var (pid, bucket) in _processes)
        {
            token.ThrowIfCancellationRequested();
            if (!alive.Contains(pid)) continue;
            matched = checked(matched + bucket.Matched);
            if (bucket.Heap is null) continue;
            foreach (var candidate in bucket.Heap.Items)
            {
                if ((visited++ & 63) == 0) token.ThrowIfCancellationRequested();
                global.Add(candidate);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(global.SortedCopy(), matched, matched > IpHelperConnections.MaximumConnections, CandidateBudgetExceeded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processes.Clear();
        _budget.Release(_reserved); _reserved = 0;
    }
}

internal sealed record EndpointProcessSelection(ProcessIdentity[] Identities, int SkippedByProcessLimit, int UnverifiedProcesses)
{
    // The UI supplies distinct identities. Bound normalization too, so an oversized input
    // cannot allocate an unbounded GroupBy/Distinct cache before the native scan limit.
    // Even a conflicting identity beyond the cap invalidates an already selected PID.
    internal static EndpointProcessSelection Create(IReadOnlyList<ProcessIdentity> source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var seen = new HashSet<ProcessIdentity>(IpHelperConnections.MaximumProcesses);
        var groups = new Dictionary<int, (ProcessIdentity First, int Count, bool Conflict)>(IpHelperConnections.MaximumProcesses);
        int skipped = 0;
        foreach (var identity in source)
        {
            token.ThrowIfCancellationRequested();
            if (seen.Contains(identity)) continue;
            if (seen.Count >= IpHelperConnections.MaximumProcesses)
            {
                skipped++;
                if (groups.TryGetValue(identity.Pid, out var previous) && previous.First != identity)
                    groups[identity.Pid] = (previous.First, previous.Count, true);
                continue;
            }
            seen.Add(identity);
            if (groups.TryGetValue(identity.Pid, out var group))
                groups[identity.Pid] = (group.First, group.Count + 1, true);
            else groups.Add(identity.Pid, (identity, 1, false));
        }
        int unverified = 0;
        var selected = new List<ProcessIdentity>(groups.Count);
        foreach (var group in groups.Values)
        {
            if (group.Conflict) unverified += group.Count;
            else selected.Add(group.First);
        }
        return new(selected.ToArray(), skipped, unverified);
    }
}

internal sealed class EndpointNativeBuffer : IDisposable
{
    private readonly EndpointWorkBudget _budget;
    private readonly int _length;
    internal nint Pointer { get; private set; }

    private EndpointNativeBuffer(int length, EndpointWorkBudget budget)
    {
        _budget = budget; _length = length;
        Pointer = Marshal.AllocHGlobal(length);
    }

    internal static bool TryCreate(int length, EndpointWorkBudget budget, out EndpointNativeBuffer? buffer)
    {
        buffer = null;
        if (!budget.TryReserve(length)) return false;
        try { buffer = new(length, budget); return true; }
        catch { budget.Release(length); throw; }
    }

    public void Dispose()
    {
        if (Pointer == 0) return;
        Marshal.FreeHGlobal(Pointer); Pointer = 0;
        _budget.Release(_length);
    }
}
