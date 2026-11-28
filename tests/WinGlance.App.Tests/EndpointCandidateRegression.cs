using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinGlance.App.Services;
using WinGlance.Core;

internal static class EndpointCandidateRegression
{
    internal static void Run(Action<bool, string> check)
    {
        check(Unsafe.SizeOf<EndpointCandidate>() <= 64 && !RuntimeHelpers.IsReferenceOrContainsReferences<EndpointCandidate>(),
            "endpoint candidates fit the reference-free 64-byte storage contract");
        ReferenceAndExitCases(check);
        OrderingAndNativeRows(check);
        CapacityAndCoverage(check);
        NativeTableBounds(check);
    }

    private static EndpointCandidate Row(int pid, int value, EndpointProtocol protocol = EndpointProtocol.Tcp4) =>
        new(0, (uint)value, 0, (uint)(value + 1), 0, 0, pid, 5, (ushort)(value % 65536), 443, protocol);

    private static EndpointCandidate[] Reference(IEnumerable<EndpointCandidate> source, ISet<int> alive) =>
        source.Where(x => alive.Contains(x.Pid)).OrderBy(x => (byte)x.Protocol).ThenBy(x => x.Pid)
            .ThenBy(x => x.LocalHigh).ThenBy(x => x.LocalLow).ThenBy(x => x.LocalScope).ThenBy(x => x.LocalPort)
            .ThenBy(x => x.HasRemote).ThenBy(x => x.HasRemote ? x.RemoteHigh : 0)
            .ThenBy(x => x.HasRemote ? x.RemoteLow : 0).ThenBy(x => x.HasRemote ? x.RemoteScope : 0)
            .ThenBy(x => x.HasRemote ? x.RemotePort : 0).ThenBy(x => x.State).Take(200).ToArray();

    private static void ReferenceAndExitCases(Action<bool, string> check)
    {
        var random = new Random(3701);
        var source = Enumerable.Range(1, 8).SelectMany(pid => Enumerable.Range(0, 351).Select(_ =>
            Row(pid, random.Next(1, 60000), (EndpointProtocol)random.Next(4)) with
            {
                LocalScope = (uint)random.Next(8), RemoteScope = (uint)random.Next(8),
                State = (uint)random.Next(2, 13), LocalHigh = (ulong)random.Next(3)
            })).ToArray();
        HashSet<int>[] survivors = [Enumerable.Range(1, 8).ToHashSet(), [8], [2, 4, 6], []];
        var orders = new[] { source, source.Reverse().ToArray(), source.OrderBy(_ => random.Next()).ToArray() };
        bool allCorrect = true;
        foreach (var order in orders)
        {
            var budget = new EndpointWorkBudget();
            using var candidates = new EndpointCandidateSet(budget);
            foreach (int pid in Enumerable.Range(1, 8)) candidates.AddProcess(pid);
            foreach (var candidate in order) candidates.Add(candidate);
            foreach (var alive in survivors)
            {
                var selected = candidates.Select(alive, CancellationToken.None);
                allCorrect &= selected.Candidates.SequenceEqual(Reference(source, alive)) &&
                    selected.MatchedCount == source.Count(x => alive.Contains(x.Pid)) &&
                    selected.DisplayTruncated == (selected.MatchedCount > 200) && !selected.CandidateBudgetExceeded;
            }
        }
        check(allCorrect, "per-process top 200 matches full numeric reference sorting under scan permutations and process exits");

        using var exits = new EndpointCandidateSet(new());
        exits.AddProcess(1); exits.AddProcess(2);
        foreach (int i in Enumerable.Range(0, 240)) exits.Add(Row(1, i));
        foreach (int i in Enumerable.Range(0, 10)) exits.Add(Row(2, i));
        var afterExit = exits.Select(new HashSet<int> { 2 }, CancellationToken.None);
        check(afterExit.Candidates.Length == 10 && afterExit.MatchedCount == 10 && !afterExit.DisplayTruncated &&
              afterExit.Candidates.All(x => x.Pid == 2),
            "exiting process that previously filled the display cannot discard another process's surviving endpoints");

        using var boundary = new EndpointCandidateSet(new());
        boundary.AddProcess(7);
        foreach (int i in Enumerable.Range(1, 200)) boundary.Add(Row(7, i));
        var exact = boundary.Select(new HashSet<int> { 7 }, CancellationToken.None);
        boundary.Add(Row(7, 0));
        var overflow = boundary.Select(new HashSet<int> { 7 }, CancellationToken.None);
        check(exact.Candidates.Length == 200 && !exact.DisplayTruncated && overflow.MatchedCount == 201 &&
              overflow.Candidates.Length == 200 && overflow.DisplayTruncated && overflow.Candidates[0].LocalLow == 0,
            "200/201 endpoint boundary preserves smallest numeric candidates and independent display truncation");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool observed = false;
        try { exits.Select(new HashSet<int> { 2 }, cancelled.Token); }
        catch (OperationCanceledException) { observed = true; }
        check(observed, "cancelled survivor selection stops before final endpoint materialization");
    }

    private static void OrderingAndNativeRows(Action<bool, string> check)
    {
        var baseRow = Row(2, 9, EndpointProtocol.Tcp6) with { LocalHigh = 0xFE80000000000000, LocalLow = 1, LocalScope = 3 };
        check(baseRow.CompareTo(baseRow with { LocalScope = 4 }) < 0 &&
              baseRow.CompareTo(baseRow with { LocalPort = (ushort)(baseRow.LocalPort + 1) }) < 0 &&
              baseRow.CompareTo(baseRow with { RemoteScope = 1 }) < 0 &&
              baseRow.CompareTo(baseRow with { RemotePort = 444 }) < 0 &&
              baseRow.CompareTo(baseRow with { State = 6 }) < 0 &&
              Row(2, 9).CompareTo(Row(2, 10)) < 0,
            "endpoint ordering includes numeric addresses, IPv6 scope, ports, and state without lexicographic IP sorting");

        var budget = new EndpointWorkBudget();
        check(EndpointNativeBuffer.TryCreate(56, budget, out var memory), "native candidate fixture allocates within explicit work budget");
        using (memory)
        {
            byte[] bytes = new byte[56];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, 0xFE80000000000000);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 3);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 443);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), 0x20010DB800000000);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), 7);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(44), 50000);
            BitConverter.TryWriteBytes(bytes.AsSpan(48), 5u);
            BitConverter.TryWriteBytes(bytes.AsSpan(52), 42);
            Marshal.Copy(bytes, 0, memory!.Pointer, bytes.Length);
            var candidate = IpHelperConnections.ReadCandidate(memory.Pointer, 42, EndpointProtocol.Tcp6);
            var row = IpHelperConnections.Materialize(candidate, new(42, 100));
            check(row.LocalEndpoint == "[fe80::1%3]:443" && row.RemoteEndpoint == "[2001:db8::2%7]:50000" && row.State == "已连接",
                "compact IPv6 native parsing preserves nonzero scope IDs and network-order ports");
            Marshal.WriteInt32(memory.Pointer, 48, 2);
            var listening = IpHelperConnections.ReadCandidate(memory.Pointer, 42, EndpointProtocol.Tcp6);
            check(!listening.HasRemote && listening.RemoteHigh == 0 && listening.RemoteLow == 0 &&
                  listening.RemoteScope == 0 && listening.RemotePort == 0 &&
                  IpHelperConnections.Materialize(listening, new(42, 100)).RemoteEndpoint is null,
                "TCP listeners normalize unused remote native fields before ordering and display");

            using var heap = new EndpointCandidateSet(budget); heap.AddProcess(42);
            for (int i = 0; i < 1000; i++) heap.Add(IpHelperConnections.ReadCandidate(memory.Pointer, 42, EndpointProtocol.Tcp6));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) heap.Add(IpHelperConnections.ReadCandidate(memory.Pointer, 42, EndpointProtocol.Tcp6));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            check(allocated < 1024, "native row decoding and full-heap insertion avoid per-row managed allocation");
        }
        check(budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve,
            "native fixture and candidate arrays release their explicit active storage reservations");
    }

    private static void CapacityAndCoverage(Action<bool, string> check)
    {
        var identities = Enumerable.Range(1, 513).Select(pid => new ProcessIdentity(pid, pid + 1000)).ToArray();
        var selection = EndpointProcessSelection.Create(identities, CancellationToken.None);
        var duplicate = EndpointProcessSelection.Create([identities[0], identities[0]], CancellationToken.None);
        var conflict = EndpointProcessSelection.Create([identities[0], identities[0] with { StartTimeUtcTicks = 9 }], CancellationToken.None);
        var lateConflict = EndpointProcessSelection.Create(identities[..512].Append(identities[0] with { StartTimeUtcTicks = 9 }).ToArray(), CancellationToken.None);
        check(selection.Identities.Length == 512 && selection.SkippedByProcessLimit == 1 && selection.UnverifiedProcesses == 0 &&
              duplicate.Identities.Length == 1 && conflict.Identities.Length == 0 && conflict.UnverifiedProcesses == 2 &&
              lateConflict.Identities.Length == 511 && lateConflict.UnverifiedProcesses == 1 && lateConflict.SkippedByProcessLimit == 1,
            "512/513 target cap is distinct from duplicate or conflicting process creation identities");

        var budget = new EndpointWorkBudget();
        using (var full = new EndpointCandidateSet(budget))
        {
            check(EndpointNativeBuffer.TryCreate((int)IpHelperConnections.MaximumTableBytes, budget, out var memory),
                "maximum native table can coexist with bounded process candidates");
            using (memory)
            {
                foreach (int pid in Enumerable.Range(1, 512))
                {
                    full.AddProcess(pid);
                    foreach (int i in Enumerable.Range(0, 201)) full.Add(Row(pid, i));
                }
                check(full.RetainedCount == 512 * 200 && !full.AddProcess(513) && !full.CandidateBudgetExceeded &&
                      budget.HighWaterBytes <= EndpointWorkBudget.MaximumBytes &&
                      budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve + IpHelperConnections.MaximumTableBytes +
                          512L * EndpointCandidateSet.ProcessBlockBytes,
                    "512 full process heaps plus an 8 MiB table stay inside the 16 MiB explicit active-storage budget");
            }
        }
        check(budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve,
            "disposing maximum candidate storage and table releases their complete budget charge");

        var limitedBudget = new EndpointWorkBudget(EndpointWorkBudget.MetadataAndOutputReserve + EndpointCandidateSet.ProcessBlockBytes);
        using var limited = new EndpointCandidateSet(limitedBudget);
        limited.AddProcess(1); limited.AddProcess(2); limited.Add(Row(1, 1)); limited.Add(Row(2, 1));
        var limitedResult = limited.Select(new HashSet<int> { 2 }, CancellationToken.None);
        var result = new ConnectionReadResult(DateTimeOffset.Now, [], 1, [], limitedResult.DisplayTruncated,
            limitedResult.MatchedCount, 0, limitedResult.CandidateBudgetExceeded, limitedBudget.HighWaterBytes);
        check(limitedResult.Candidates.Length == 0 && limitedResult.MatchedCount == 1 && limitedResult.CandidateBudgetExceeded &&
              !limitedResult.DisplayTruncated && result.IsPartial && result.EmptyMessage.Contains("预算") &&
              !result.Description.Contains("显示前 0 条") && limitedBudget.HighWaterBytes <= limitedBudget.Limit,
            "candidate budget refusal keeps counts but reports partial unshown endpoints without false display-limit claims");
        var skipped = new ConnectionReadResult(DateTimeOffset.Now, [], 0, [], false, 0, SkippedByProcessLimit: 1);
        var unknown = new ConnectionReadResult(DateTimeOffset.Now, [], 0, ["TCP/IPv6"], false, 0);
        var empty = new ConnectionReadResult(DateTimeOffset.Now, [], 0, [], false, 0);
        check(skipped.IsPartial && !skipped.DisplayTruncated && skipped.Description.Contains("扫描上限") &&
              !skipped.Description.Contains("无权读取") && unknown.IsPartial &&
              empty.EmptyMessage.Contains("当前没有") && !unknown.EmptyMessage.Contains("当前没有"),
            "process limit, unavailable tables, and valid empty endpoint results have distinct coverage semantics");
    }

    private static void NativeTableBounds(Action<bool, string> check)
    {
        var budget = new EndpointWorkBudget();
        int calls = 0, captured = 0;
        uint Oversized(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved)
        { calls++; size = IpHelperConnections.MaximumTableBytes + 1; return 122; }
        var status = IpHelperConnections.ReadNativeTable(EndpointProtocol.Tcp4, Oversized, _ => true, _ => captured++, budget, CancellationToken.None);
        check(status == IpHelperConnections.EndpointTableStatus.Unavailable && calls == 1 && captured == 0 &&
              budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve,
            "oversized native tables are rejected before allocation or row decoding");

        calls = 0;
        uint Growing(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved)
        { calls++; size = (uint)(calls * 64); return 122; }
        status = IpHelperConnections.ReadNativeTable(EndpointProtocol.Tcp4, Growing, _ => true, _ => captured++, budget, CancellationToken.None);
        check(status == IpHelperConnections.EndpointTableStatus.Unavailable && calls == 4 &&
              budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve &&
              budget.HighWaterBytes == EndpointWorkBudget.MetadataAndOutputReserve + 192,
            "native table growth has one probe and three attempts with no overlapping retry buffers");

        calls = 0;
        uint InvalidCount(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved)
        {
            calls++; size = 4;
            if (table == 0) return 122;
            Marshal.WriteInt32(table, int.MaxValue); return 0;
        }
        status = IpHelperConnections.ReadNativeTable(EndpointProtocol.Tcp4, InvalidCount, _ => true, _ => captured++, budget, CancellationToken.None);
        check(status == IpHelperConnections.EndpointTableStatus.Unavailable && captured == 0 &&
              budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve,
            "invalid native row count cannot read past the allocated table and releases its buffer");

        using var cancel = new CancellationTokenSource();
        calls = 0;
        uint CancelDuringRead(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved)
        {
            calls++; size = 4;
            if (table == 0) return 122;
            cancel.Cancel(); Marshal.WriteInt32(table, 0); return 0;
        }
        bool cancelled = false;
        try { IpHelperConnections.ReadNativeTable(EndpointProtocol.Tcp4, CancelDuringRead, _ => true, _ => captured++, budget, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && calls == 2 && budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve,
            "cancellation immediately after native read releases its table before publishing rows");

        calls = 0;
        uint ValidRow(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved)
        {
            calls++; size = 16;
            if (table == 0) return 122;
            Marshal.WriteInt32(table, 1); Marshal.WriteInt32(table, 4, 0x0100007f);
            Marshal.WriteInt32(table, 8, 0x5000); Marshal.WriteInt32(table, 12, 42); return 0;
        }
        bool threw = false;
        try { IpHelperConnections.ReadNativeTable(EndpointProtocol.Udp4, ValidRow, pid => pid == 42,
            _ => throw new InvalidOperationException("controlled consumer failure"), budget, CancellationToken.None); }
        catch (InvalidOperationException) { threw = true; }
        check(threw && calls == 2 && budget.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve,
            "row consumer exceptions cannot retain native table buffers");
        var noBuffer = new EndpointWorkBudget(EndpointWorkBudget.MetadataAndOutputReserve);
        status = IpHelperConnections.ReadNativeTable(EndpointProtocol.Udp4, ValidRow, _ => true, _ => captured++, noBuffer, CancellationToken.None);
        check(status == IpHelperConnections.EndpointTableStatus.WorkBudgetExceeded &&
              noBuffer.CurrentBytes == EndpointWorkBudget.MetadataAndOutputReserve,
            "native work-budget refusal is explicit and does not allocate or fall back to unbounded storage");
    }
}
