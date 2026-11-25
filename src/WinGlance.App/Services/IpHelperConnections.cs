using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinGlance.Core;

namespace WinGlance.App.Services;

public sealed record ProcessConnection(string Protocol, ProcessIdentity Process, string LocalEndpoint,
    string? RemoteEndpoint, string State);

internal sealed record ConnectionReadResult(DateTimeOffset ObservedAt, IReadOnlyList<ProcessConnection> Connections,
    int UnverifiedProcesses, IReadOnlyList<string> UnavailableTables, bool Truncated, int MatchedCount)
{
    public string Description
    {
        get
        {
            string text = $"{MatchedCount} 条端点 · {ObservedAt.LocalDateTime:HH:mm:ss}";
            if (Truncated) text += $" · 显示前 {Connections.Count} 条";
            if (UnverifiedProcesses > 0) text += $" · {UnverifiedProcesses} 个进程已退出或无权读取";
            if (UnavailableTables.Count > 0) text += $" · {string.Join("、", UnavailableTables)} 不可用";
            return text;
        }
    }
}

// IP Helper snapshots are requested only by the open details view. They do not capture traffic,
// perform DNS lookups, or require the enhanced collector.
internal static class IpHelperConnections
{
    internal const int MaximumConnections = 200;
    internal const int MaximumProcesses = 512;
    private const uint InsufficientBuffer = 122;
    private const uint MaximumTableBytes = 8 * 1024 * 1024;
    private const long FileTimeEpochTicks = 504911232000000000;

    internal static Task<ConnectionReadResult> ReadAsync(IReadOnlyList<ProcessIdentity> identities, CancellationToken token) =>
        Task.Run(() => Read(identities, token), token);

    private static ConnectionReadResult Read(IReadOnlyList<ProcessIdentity> identities, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var handles = new Dictionary<int, (ProcessIdentity Identity, SafeProcessHandle Handle)>();
        var rows = new List<ProcessConnection>();
        var counts = new Dictionary<int, int>();
        var unavailable = new List<string>();
        int unverified = 0, attempted = 0;
        bool limitedProcesses = identities.Count > MaximumProcesses;
        try
        {
            // Conflicting generations of one PID are not attributable to this selection.
            foreach (var group in identities.Distinct().GroupBy(x => x.Pid))
            {
                token.ThrowIfCancellationRequested();
                if (group.Count() != 1 || attempted++ >= MaximumProcesses) { unverified += group.Count(); continue; }
                var identity = group.First();
                var handle = OpenVerifiedProcess(identity);
                if (handle is null) { unverified++; continue; }
                handles.Add(identity.Pid, (identity, handle));
            }
            if (handles.Count == 0)
                return new(DateTimeOffset.Now, [], unverified, [], limitedProcesses, 0);

            ReadTable<Tcp4Table, Tcp4Row>(GetExtendedTcpTable, 2, 5, "TCP/IPv4", nameof(Tcp4Row.Pid),
                row => row.Pid, (row, identity) => new("TCP4", identity, Endpoint4(row.LocalAddress, row.LocalPort),
                    row.State == 2 ? null : Endpoint4(row.RemoteAddress, row.RemotePort), TcpState(row.State)));
            ReadTable<Tcp6Table, Tcp6Row>(GetExtendedTcpTable, 23, 5, "TCP/IPv6", nameof(Tcp6Row.Pid),
                row => row.Pid, (row, identity) => new("TCP6", identity, Endpoint6(row.LocalAddress, row.LocalScope, row.LocalPort),
                    row.State == 2 ? null : Endpoint6(row.RemoteAddress, row.RemoteScope, row.RemotePort), TcpState(row.State)));
            ReadTable<Udp4Table, Udp4Row>(GetExtendedUdpTable, 2, 1, "UDP/IPv4", nameof(Udp4Row.Pid),
                row => row.Pid, (row, identity) => new("UDP4", identity, Endpoint4(row.LocalAddress, row.LocalPort), null, "本地绑定"));
            ReadTable<Udp6Table, Udp6Row>(GetExtendedUdpTable, 23, 1, "UDP/IPv6", nameof(Udp6Row.Pid),
                row => row.Pid, (row, identity) => new("UDP6", identity, Endpoint6(row.LocalAddress, row.LocalScope, row.LocalPort), null, "本地绑定"));

            // A retained handle ties both checks to the same kernel process object. If it exited
            // while the tables were read, none of its rows may be attributed to a reused PID.
            var stillAlive = new HashSet<int>();
            foreach (var (pid, held) in handles)
            {
                token.ThrowIfCancellationRequested();
                if (WaitForSingleObject(held.Handle, 0) == 258) stillAlive.Add(pid);
                else unverified++;
            }
            var visible = rows.Where(x => stillAlive.Contains(x.Process.Pid))
                .OrderBy(x => x.Protocol, StringComparer.Ordinal).ThenBy(x => x.Process.Pid)
                .ThenBy(x => x.LocalEndpoint, StringComparer.Ordinal).ThenBy(x => x.RemoteEndpoint, StringComparer.Ordinal).ToArray();
            int matched = counts.Where(x => stillAlive.Contains(x.Key)).Sum(x => x.Value);
            return new(DateTimeOffset.Now, visible, unverified, unavailable, limitedProcesses || matched > visible.Length, matched);
        }
        finally
        {
            foreach (var item in handles.Values) item.Handle.Dispose();
        }

        void ReadTable<TTable, TRow>(NativeTable read, uint family, uint tableClass, string label, string pidField,
            Func<TRow, uint> owner, Func<TRow, ProcessIdentity, ProcessConnection> convert)
            where TTable : struct where TRow : struct
        {
            token.ThrowIfCancellationRequested();
            uint size = 0;
            uint status = read(0, ref size, false, family, tableClass, 0);
            if (status == 0 && size == 0) return;
            if (status != InsufficientBuffer && status != 0) { unavailable.Add(label); return; }
            int offset = Marshal.OffsetOf<TTable>("FirstRow").ToInt32();
            int rowSize = Marshal.SizeOf<TRow>(), pidOffset = Marshal.OffsetOf<TRow>(pidField).ToInt32();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (size < offset || size > MaximumTableBytes) { unavailable.Add(label); return; }
                uint allocated = size;
                nint buffer = Marshal.AllocHGlobal((int)allocated);
                try
                {
                    status = read(buffer, ref size, false, family, tableClass, 0);
                    if (status == InsufficientBuffer) continue;
                    if (status != 0) { unavailable.Add(label); return; }
                    uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                    if (count > (allocated - offset) / rowSize) { unavailable.Add(label); return; }
                    for (uint i = 0; i < count; i++)
                    {
                        if ((i & 63) == 0) token.ThrowIfCancellationRequested();
                        nint pointer = buffer + offset + checked((int)i * rowSize);
                        int pid = Marshal.ReadInt32(pointer, pidOffset);
                        if (!handles.TryGetValue(pid, out var held)) continue;
                        counts[pid] = counts.GetValueOrDefault(pid) + 1;
                        if (rows.Count >= MaximumConnections) continue;
                        var row = Marshal.PtrToStructure<TRow>(pointer);
                        if (owner(row) != (uint)pid) continue;
                        rows.Add(convert(row, held.Identity));
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            unavailable.Add(label);
        }
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

    internal static ushort Port(uint networkOrder) => (ushort)(((networkOrder & 0xff) << 8) | ((networkOrder >> 8) & 0xff));
    internal static uint Scope(uint networkOrder) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(networkOrder) : networkOrder;
    private static string Endpoint4(uint address, uint port) => $"{new IPAddress(BitConverter.GetBytes(address))}:{Port(port)}";
    private static string Endpoint6(Address6 address, uint scope, uint port) => $"[{new IPAddress(address.Bytes(), Scope(scope))}]:{Port(port)}";
    private static string TcpState(uint value) => value switch
    {
        1 => "已关闭", 2 => "监听", 3 => "SYN 已发送", 4 => "SYN 已接收", 5 => "已连接",
        6 => "FIN 等待 1", 7 => "FIN 等待 2", 8 => "关闭等待", 9 => "正在关闭",
        10 => "最后确认", 11 => "时间等待", 12 => "删除中", _ => $"状态 {value}"
    };

    // DWORD fields ensure the native 4-byte alignment, including the IPv6 byte arrays.
    [StructLayout(LayoutKind.Sequential)] private struct Address6
    {
        public uint A, B, C, D;
        public byte[] Bytes()
        {
            byte[] bytes = new byte[16];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), A); BitConverter.TryWriteBytes(bytes.AsSpan(4), B);
            BitConverter.TryWriteBytes(bytes.AsSpan(8), C); BitConverter.TryWriteBytes(bytes.AsSpan(12), D);
            return bytes;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Tcp4Row { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct Tcp6Row
    {
        public Address6 LocalAddress; public uint LocalScope, LocalPort;
        public Address6 RemoteAddress; public uint RemoteScope, RemotePort, State, Pid;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Udp4Row { public uint LocalAddress, LocalPort, Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct Udp6Row { public Address6 LocalAddress; public uint LocalScope, LocalPort, Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct Tcp4Table { public uint Count; public Tcp4Row FirstRow; }
    [StructLayout(LayoutKind.Sequential)] private struct Tcp6Table { public uint Count; public Tcp6Row FirstRow; }
    [StructLayout(LayoutKind.Sequential)] private struct Udp4Table { public uint Count; public Udp4Row FirstRow; }
    [StructLayout(LayoutKind.Sequential)] private struct Udp6Table { public uint Count; public Udp6Row FirstRow; }
    private delegate uint NativeTable(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(nint table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
