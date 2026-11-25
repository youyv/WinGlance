using System.Diagnostics;
using System.Runtime.InteropServices;
using WinGlance.Core;

namespace WinGlance.Windows.Telemetry;

/// <summary>All hardware interfaces, once each; excludes filter, endpoint, loopback and tunnel interfaces.</summary>
internal sealed unsafe class PhysicalNetworkAccounting
{
    private Dictionary<Guid, (ulong Luid, ulong Receive, ulong Send)> _previous = [];
    private Dictionary<Guid, (ulong Luid, ulong Receive, ulong Send, bool Connected)> _devicePrevious = [];
    private long _lastTimestamp;
    private DateTimeOffset _lastUtc;

    internal NetworkAccountingSample Sample(bool reset)
    {
        long now = Stopwatch.GetTimestamp();
        var endUtc = DateTimeOffset.UtcNow;
        var startUtc = _lastTimestamp == 0 ? endUtc : _lastUtc;
        double seconds = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        _lastUtc = endUtc;
        var rows = new List<NetworkInterfaceAccounting>();
        var next = new Dictionary<Guid, (ulong Luid, ulong Receive, ulong Send)>();
        var devices = new List<NetworkInterfaceSample>();
        var deviceNext = new Dictionary<Guid, (ulong Luid, ulong Receive, ulong Send, bool Connected)>();
        nint table = 0;
        uint error = GetIfTable2(out table);
        if (error != 0 || table == 0)
        {
            _previous.Clear();
            _devicePrevious.Clear();
            if (table != 0) FreeMibTable(table);
            return Failure(MetricState.Error, seconds, $"物理网卡计数器不可用（{error}）");
        }
        try
        {
            uint count = *(uint*)table;
            if (count > 16384) { _previous.Clear(); _devicePrevious.Clear(); return Failure(MetricState.Error, seconds, "网络接口数量超出采集上限"); }
            var first = (IfRow*)((byte*)table + Marshal.OffsetOf<IfTable>(nameof(IfTable.First)).ToInt32());
            bool continuous = !reset && AccountingMath.Continuous(seconds);
            for (uint i = 0; i < count; i++)
            {
                var row = first + i;
                byte flags = row->Flags;
                // InterfaceAndOperStatusFlags identifies hidden filter/end-point interfaces without
                // guessing from localized driver names. User-facing virtual adapters remain selectable.
                if ((flags & 2) != 0 || (flags & 128) != 0 || row->Type == 24) continue;
                Guid id = row->InterfaceGuid;
                if (deviceNext.ContainsKey(id)) continue;
                bool physical = (flags & 1) != 0 && row->Type != 131;
                bool connected = row->OperStatus == 1;
                string name = new((char*)row->Alias);
                string description = new((char*)row->Description);
                if (name.Length == 0) name = description;
                deviceNext[id] = (row->InterfaceLuid, row->InOctets, row->OutOctets, connected);
                bool deviceExists = _devicePrevious.TryGetValue(id, out var deviceOld);
                bool deviceKnown = continuous && connected && deviceExists &&
                    deviceOld.Connected && deviceOld.Luid == row->InterfaceLuid;
                double? deviceDown = deviceKnown && row->InOctets >= deviceOld.Receive && row->InOctets - deviceOld.Receive <= long.MaxValue
                    ? (row->InOctets - deviceOld.Receive) / seconds : null;
                double? deviceUp = deviceKnown && row->OutOctets >= deviceOld.Send && row->OutOctets - deviceOld.Send <= long.MaxValue
                    ? (row->OutOctets - deviceOld.Send) / seconds : null;
                bool deviceReady = deviceDown.HasValue && deviceUp.HasValue;
                devices.Add(new(id.ToString("B").ToUpperInvariant(), name, deviceDown, deviceUp,
                    !connected ? MetricState.Unsupported : deviceReady ? MetricState.Ready : MetricState.WarmingUp,
                    physical, connected, connected ? description : "网络接口未连接"));
                if (!physical) continue;
                next[id] = (row->InterfaceLuid, row->InOctets, row->OutOctets);
                bool exists = _previous.TryGetValue(id, out var old);
                bool known = continuous && exists && old.Luid == row->InterfaceLuid;
                long? received = known && row->InOctets >= old.Receive && row->InOctets - old.Receive <= long.MaxValue
                    ? (long)(row->InOctets - old.Receive) : null;
                long? sent = known && row->OutOctets >= old.Send && row->OutOctets - old.Send <= long.MaxValue
                    ? (long)(row->OutOctets - old.Send) : null;
                bool ready = received.HasValue && sent.HasValue;
                rows.Add(new(id.ToString("D"), name, row->InterfaceLuid, received, sent, seconds,
                    ready ? MetricState.Ready : MetricState.WarmingUp,
                    ready ? null : "接口首次出现、计数重置或采集间断，已重新建立基线"));
            }
            // A missing or new interface is a scope boundary: do not mix partial hardware totals with complete totals.
            bool sameScope = _previous.Count == next.Count && _previous.Keys.All(next.ContainsKey);
            _previous = next;
            _devicePrevious = deviceNext;
            if (rows.Count == 0) return Failure(MetricState.Unsupported, seconds, "未发现可统计的硬件网络接口") with { Devices = devices };
            bool complete = continuous && sameScope && rows.All(x => x.State == MetricState.Ready);
            long? down = complete ? CheckedSum(rows.Select(x => x.DownloadBytesDelta!.Value)) : null;
            long? up = complete ? CheckedSum(rows.Select(x => x.UploadBytesDelta!.Value)) : null;
            string scope = "硬件网卡（含已断开接口，排除隧道/过滤/虚拟端点）：" + string.Join("、", rows.Select(x => $"{x.Name} [{x.Id}]"));
            string? detail = complete ? null : "接口范围变化或计数不连续，本窗口总量不可用";
            return new(rows, scope,
                AccountingMath.Bytes(down, seconds, continuous, MetricState.WarmingUp, detail) with { StartUtc = startUtc, EndUtc = endUtc },
                AccountingMath.Bytes(up, seconds, continuous, MetricState.WarmingUp, detail) with { StartUtc = startUtc, EndUtc = endUtc })
            { Devices = devices };
        }
        finally { FreeMibTable(table); }
    }

    private static long? CheckedSum(IEnumerable<long> values)
    {
        try { long total = 0; foreach (long value in values) total = checked(total + value); return total; }
        catch (OverflowException) { return null; }
    }

    private static NetworkAccountingSample Failure(MetricState state, double seconds, string detail) =>
        new([], detail, AccountingMath.Missing(state, seconds, detail), AccountingMath.Missing(state, seconds, detail));

    [StructLayout(LayoutKind.Sequential)] private struct IfTable { public uint Count; public IfRow First; }
    [StructLayout(LayoutKind.Sequential)] private struct IfRow
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed ushort Alias[257];
        public fixed ushort Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos;
        public ulong InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors;
        public ulong OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetIfTable2(out nint table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(nint table);
}

internal sealed record NetworkAccountingSample(IReadOnlyList<NetworkInterfaceAccounting> Interfaces,
    string Scope, MetricAccounting Download, MetricAccounting Upload)
{
    internal IReadOnlyList<NetworkInterfaceSample> Devices { get; init; } = [];
}
