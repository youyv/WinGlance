using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinGlance.Core;

namespace WinGlance.App.Services;

public sealed record ProcessConnection(string Protocol, ProcessIdentity Process, string LocalEndpoint,
    string? RemoteEndpoint, string State);

internal sealed record ConnectionReadResult(DateTimeOffset ObservedAt, IReadOnlyList<ProcessConnection> Connections,
    int UnverifiedProcesses, IReadOnlyList<string> UnavailableTables, bool Truncated, int MatchedCount,
    int SkippedByProcessLimit = 0, bool CandidateBudgetExceeded = false, long WorkStorageHighWaterBytes = 0)
{
    // Truncated now describes the display limit only; process/table/budget gaps are separate.
    public bool DisplayTruncated => Truncated;
    public bool IsPartial => UnverifiedProcesses > 0 || SkippedByProcessLimit > 0 ||
        UnavailableTables.Count > 0 || CandidateBudgetExceeded;
    public string EmptyMessage => CandidateBudgetExceeded && MatchedCount > 0
        ? "受查询预算限制，未能展示已观测端点"
        : IsPartial ? "未能完整查询连接，详见上方提示" : "当前没有 TCP 连接或 UDP 绑定";
    public string Description
    {
        get
        {
            string text = $"{(IsPartial ? "已观测 " : "")}{MatchedCount} 条端点 · {ObservedAt.LocalDateTime:HH:mm:ss}";
            if (Truncated && !CandidateBudgetExceeded) text += $" · 按数值顺序显示前 {Connections.Count} 条";
            if (CandidateBudgetExceeded) text += $" · 查询预算不足，仅取得 {Connections.Count} 条可显示端点";
            if (SkippedByProcessLimit > 0) text += $" · {SkippedByProcessLimit} 个进程候选因扫描上限未读取";
            if (UnverifiedProcesses > 0) text += $" · {UnverifiedProcesses} 个进程身份无法验证、已退出或无权读取";
            if (UnavailableTables.Count > 0) text += $" · {string.Join("、", UnavailableTables)} 不可用";
            return text;
        }
    }
}

// IP Helper snapshots are requested only by the open details view. They do not capture traffic,
// perform DNS lookups, or require the enhanced collector. Four tables are sequential snapshots,
// never represented as one atomic operating-system snapshot.
internal static class IpHelperConnections
{
    internal const int MaximumConnections = 200;
    internal const int MaximumProcesses = 512;
    internal const uint MaximumTableBytes = 8 * 1024 * 1024;
    private const uint InsufficientBuffer = 122;
    private const long FileTimeEpochTicks = 504911232000000000;

    internal static Task<ConnectionReadResult> ReadAsync(IReadOnlyList<ProcessIdentity> identities, CancellationToken token) =>
        Task.Run(() => Read(identities, token), token);

    private static ConnectionReadResult Read(IReadOnlyList<ProcessIdentity> identities, CancellationToken token)
    {
        long started = DiagnosticTrace.Start(), allocated = DiagnosticTrace.Allocated();
        try { return ReadCore(identities, token); }
        finally { DiagnosticTrace.End("app.endpointQuery", started, allocated, identities.Count); }
    }

    private static ConnectionReadResult ReadCore(IReadOnlyList<ProcessIdentity> identities, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var budget = new EndpointWorkBudget();
        using var candidates = new EndpointCandidateSet(budget);
        var handles = new Dictionary<int, (ProcessIdentity Identity, SafeProcessHandle Handle)>(MaximumProcesses);
        var unavailable = new List<string>(4);
        var targets = EndpointProcessSelection.Create(identities, token);
        int unverified = targets.UnverifiedProcesses;
        try
        {
            foreach (var identity in targets.Identities)
            {
                token.ThrowIfCancellationRequested();
                var handle = OpenVerifiedProcess(identity);
                if (handle is null) { unverified++; continue; }
                handles.Add(identity.Pid, (identity, handle));
                candidates.AddProcess(identity.Pid);
            }
            if (handles.Count == 0)
                return new(DateTimeOffset.Now, [], unverified, [], false, 0,
                    targets.SkippedByProcessLimit, false, budget.HighWaterBytes);

            ReadTable(GetExtendedTcpTable, "TCP/IPv4", EndpointProtocol.Tcp4);
            ReadTable(GetExtendedTcpTable, "TCP/IPv6", EndpointProtocol.Tcp6);
            ReadTable(GetExtendedUdpTable, "UDP/IPv4", EndpointProtocol.Udp4);
            ReadTable(GetExtendedUdpTable, "UDP/IPv6", EndpointProtocol.Udp6);

            // Retained handles tie both checks to the same kernel process object. A PID that
            // exited during table reads is removed before selecting the global top 200.
            var stillAlive = new HashSet<int>(handles.Count);
            foreach (var (pid, held) in handles)
            {
                token.ThrowIfCancellationRequested();
                if (WaitForSingleObject(held.Handle, 0) == 258) stillAlive.Add(pid);
                else unverified++;
            }
            var selected = candidates.Select(stillAlive, token);
            var visible = new ProcessConnection[selected.Candidates.Length];
            for (int i = 0; i < visible.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var candidate = selected.Candidates[i];
                visible[i] = Materialize(candidate, handles[candidate.Pid].Identity);
            }
            return new(DateTimeOffset.Now, visible, unverified, unavailable, selected.DisplayTruncated,
                selected.MatchedCount, targets.SkippedByProcessLimit, selected.CandidateBudgetExceeded, budget.HighWaterBytes);
        }
        finally
        {
            foreach (var item in handles.Values) item.Handle.Dispose();
        }

        void ReadTable(NativeTable read, string label, EndpointProtocol protocol)
        {
            var status = ReadNativeTable(protocol, read, handles.ContainsKey, candidates.Add, budget, token);
            if (status != EndpointTableStatus.Ready) unavailable.Add(label);
            if (status == EndpointTableStatus.WorkBudgetExceeded) candidates.MarkBudgetExceeded();
        }
    }

    internal enum EndpointTableStatus { Ready, Unavailable, WorkBudgetExceeded }
    internal static EndpointTableStatus ReadNativeTable(EndpointProtocol protocol, NativeTable read,
        Func<int, bool> acceptPid, Action<EndpointCandidate> collect, EndpointWorkBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint family = protocol is EndpointProtocol.Tcp4 or EndpointProtocol.Udp4 ? 2u : 23u;
        uint tableClass = protocol is EndpointProtocol.Tcp4 or EndpointProtocol.Tcp6 ? 5u : 1u;
        uint size = 0;
        uint status = read(0, ref size, false, family, tableClass, 0);
        token.ThrowIfCancellationRequested();
        if (status == 0 && size == 0) return EndpointTableStatus.Ready;
        if (status != InsufficientBuffer && status != 0) return EndpointTableStatus.Unavailable;
        // All fields are DWORD-aligned, including the 16-byte IPv6 addresses.
        const int offset = sizeof(uint);
        int rowSize = protocol switch { EndpointProtocol.Tcp4 => 24, EndpointProtocol.Tcp6 => 56, EndpointProtocol.Udp4 => 12, _ => 28 };
        int pidOffset = rowSize - sizeof(uint);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (size < offset || size > MaximumTableBytes) return EndpointTableStatus.Unavailable;
            uint allocated = size;
            if (!EndpointNativeBuffer.TryCreate((int)allocated, budget, out var buffer))
                return EndpointTableStatus.WorkBudgetExceeded;
            using (buffer)
            {
                token.ThrowIfCancellationRequested();
                status = read(buffer!.Pointer, ref size, false, family, tableClass, 0);
                token.ThrowIfCancellationRequested();
                if (status == InsufficientBuffer) continue;
                if (status != 0) return EndpointTableStatus.Unavailable;
                uint count = unchecked((uint)Marshal.ReadInt32(buffer.Pointer));
                if (size < offset || size > allocated || count > (allocated - offset) / rowSize)
                    return EndpointTableStatus.Unavailable;
                for (uint i = 0; i < count; i++)
                {
                    if ((i & 63) == 0) token.ThrowIfCancellationRequested();
                    nint pointer = buffer.Pointer + offset + checked((int)i * rowSize);
                    int pid = Marshal.ReadInt32(pointer, pidOffset);
                    if (!acceptPid(pid)) continue;
                    collect(ReadCandidate(pointer, pid, protocol));
                }
                return EndpointTableStatus.Ready;
            }
        }
        return EndpointTableStatus.Unavailable;
    }

    // Reading blittable DWORDs directly avoids generic PtrToStructure boxing and per-row
    // marshaler/array allocations. Native byte layout is covered by the loopback regressions.
    internal static EndpointCandidate ReadCandidate(nint row, int pid, EndpointProtocol protocol)
    {
        uint Read(int offset) => unchecked((uint)Marshal.ReadInt32(row, offset));
        ulong High(int offset) => ((ulong)NetworkNumber(Read(offset)) << 32) | NetworkNumber(Read(offset + 4));
        if (protocol == EndpointProtocol.Tcp4)
        {
            uint state = Read(0);
            return new(0, NetworkNumber(Read(4)), 0, state == 2 ? 0 : NetworkNumber(Read(12)),
                0, 0, pid, state, Port(Read(8)), state == 2 ? (ushort)0 : Port(Read(16)), protocol);
        }
        if (protocol == EndpointProtocol.Udp4)
            return new(0, NetworkNumber(Read(0)), 0, 0, 0, 0, pid, 0, Port(Read(4)), 0, protocol);
        if (protocol == EndpointProtocol.Tcp6)
        {
            uint state = Read(48);
            bool remote = state != 2;
            return new(High(0), High(8), remote ? High(24) : 0, remote ? High(32) : 0,
                Scope(Read(16)), remote ? Scope(Read(40)) : 0, pid, state,
                Port(Read(20)), remote ? Port(Read(44)) : (ushort)0, protocol);
        }
        return new(High(0), High(8), 0, 0, Scope(Read(16)), 0, pid, 0, Port(Read(20)), 0, protocol);
    }

    internal static ProcessConnection Materialize(EndpointCandidate candidate, ProcessIdentity identity)
    {
        string protocol = candidate.Protocol switch
        {
            EndpointProtocol.Tcp4 => "TCP4", EndpointProtocol.Tcp6 => "TCP6",
            EndpointProtocol.Udp4 => "UDP4", _ => "UDP6"
        };
        return new(protocol, identity,
            Endpoint(candidate.LocalHigh, candidate.LocalLow, candidate.LocalScope, candidate.LocalPort, candidate.IsIPv6),
            candidate.HasRemote ? Endpoint(candidate.RemoteHigh, candidate.RemoteLow, candidate.RemoteScope, candidate.RemotePort, candidate.IsIPv6) : null,
            candidate.Protocol is EndpointProtocol.Tcp4 or EndpointProtocol.Tcp6 ? TcpState(candidate.State) : "本地绑定");
    }

    private static string Endpoint(ulong high, ulong low, uint scope, ushort port, bool ipv6)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!ipv6)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)low);
            return $"{new IPAddress(bytes[..4])}:{port}";
        }
        BinaryPrimitives.WriteUInt64BigEndian(bytes, high);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], low);
        return $"[{new IPAddress(bytes, scope)}]:{port}";
    }

    private static SafeProcessHandle? OpenVerifiedProcess(ProcessIdentity identity)
    {
        if (identity.Pid <= 0 || identity.StartTimeUtcTicks <= 0) return null;
        var handle = OpenProcess(0x00100000 | 0x1000, false, identity.Pid);
        if (!handle.IsInvalid && GetProcessTimes(handle, out long created, out _, out _, out _) &&
            created > 0 && created <= DateTime.MaxValue.Ticks - FileTimeEpochTicks &&
            created + FileTimeEpochTicks == identity.StartTimeUtcTicks && WaitForSingleObject(handle, 0) == 258)
            return handle;
        handle.Dispose();
        return null;
    }

    private static uint NetworkNumber(uint value) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    internal static ushort Port(uint networkOrder) => (ushort)(((networkOrder & 0xff) << 8) | ((networkOrder >> 8) & 0xff));
    internal static uint Scope(uint networkOrder) => NetworkNumber(networkOrder);
    private static string TcpState(uint value) => value switch
    {
        1 => "已关闭", 2 => "监听", 3 => "SYN 已发送", 4 => "SYN 已接收", 5 => "已连接",
        6 => "FIN 等待 1", 7 => "FIN 等待 2", 8 => "关闭等待", 9 => "正在关闭",
        10 => "最后确认", 11 => "时间等待", 12 => "删除中", _ => $"状态 {value}"
    };

    internal delegate uint NativeTable(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
