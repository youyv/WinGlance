using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinGlance.App.Services;
using WinGlance.Core;

var fixtures = new[] {
    Fixture.Create("tcp6-512x201-forward", 512, 201, EndpointProtocol.Tcp6, false),
    Fixture.Create("tcp6-512x201-reverse", 512, 201, EndpointProtocol.Tcp6, true),
    Fixture.Create("udp4-512x1000-forward", 512, 1000, EndpointProtocol.Udp4, false),
    Fixture.Create("udp4-512x1000-reverse", 512, 1000, EndpointProtocol.Udp4, true),
    Fixture.Create("exit-A240-survive-B10", 2, 240, EndpointProtocol.Tcp6, false, exits: true)
};
// Warm the actual decoder, bounded selection, native-copy adapter and output materializer.
foreach (var fixture in fixtures) for (int i = 0; i < 5; i++) fixture.Run();
var rounds = new List<object>();
for (int round = 0; round < 5; round++)
for (int index = 0; index < fixtures.Length; index++)
{
    var fixture = fixtures[(index + round) % fixtures.Length];
    int iterations = fixture.Exits ? 200 : 10;
    using var process = Process.GetCurrentProcess();
    var cpuStart = process.TotalProcessorTime;
    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    var timer = Stopwatch.StartNew();
    long highWater = 0, checksum = 0;
    for (int i = 0; i < iterations; i++)
    {
        var result = fixture.Run();
        highWater = Math.Max(highWater, result.HighWater);
        checksum += result.Matched + result.Displayed;
    }
    timer.Stop();
    long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    var cpu = process.TotalProcessorTime - cpuStart;
    var resultRow = new {
        Round = round + 1, fixture.Name, Iterations = iterations, fixture.RowCount,
        TableBytes = fixture.Table.Length, WallMillisecondsPerIteration = timer.Elapsed.TotalMilliseconds / iterations,
        CpuMillisecondsPerIteration = cpu.TotalMilliseconds / iterations, AllocatedBytesPerIteration = bytes / (double)iterations,
        ExplicitStorageHighWaterBytes = highWater, Gen0 = GC.CollectionCount(0) - gen0,
        Gen1 = GC.CollectionCount(1) - gen1, Gen2 = GC.CollectionCount(2) - gen2,
        WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64, Checksum = checksum
    };
    rounds.Add(resultRow);
    Console.WriteLine(JsonSerializer.Serialize(resultRow));
}
// Exercise the maximal table reservation together with every full candidate heap.
var maximalBudget = new EndpointWorkBudget();
using (var candidates = new EndpointCandidateSet(maximalBudget))
{
    if (!EndpointNativeBuffer.TryCreate((int)IpHelperConnections.MaximumTableBytes, maximalBudget, out var buffer)) throw new Exception("Native reserve failed.");
    using (buffer)
        for (int pid = 1; pid <= 512; pid++)
        {
            candidates.AddProcess(pid);
            for (int i = 0; i < 201; i++) candidates.Add(new(0, (ulong)i, 0, 0, 0, 0, pid, 0, (ushort)i, 0, EndpointProtocol.Udp4));
        }
}
var report = new {
    TimestampUtc = DateTimeOffset.UtcNow, CandidateBytes = Unsafe.SizeOf<EndpointCandidate>(),
    MaximumExplicitStorageBytes = EndpointWorkBudget.MaximumBytes,
    MaximumTableAnd512HeapsHighWaterBytes = maximalBudget.HighWaterBytes,
    PostDisposeReserveBytes = maximalBudget.CurrentBytes, MetadataAndOutputReserveBytes = EndpointWorkBudget.MetadataAndOutputReserve,
    Rounds = rounds,
    Notes = "Synthetic pipeline benchmark: bounded process normalization, injected native-table copy/parse, per-process Top200, supplied survivor selection, global Top200 and final endpoint string materialization. Includes owned HGlobal allocation and freeing; excludes OS table enumeration, process-handle identity/liveness calls and fixture construction. Fixed inputs are prebuilt outside measured regions. Allocated bytes are managed allocations on this thread, not committed heap or process memory; explicit storage high-water is active owned work storage, not total memory. Five rounds are interleaved; no p95 claim."
};
var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/endpoint-benchmark.json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Saved " + output);

sealed class Fixture
{
    internal required string Name;
    internal required byte[] Table;
    internal required ProcessIdentity[] Identities;
    internal required HashSet<int> Alive;
    internal required EndpointProtocol Protocol;
    internal required int RowCount;
    internal bool Exits;
    private uint Read(nint table, ref uint size, bool order, uint family, uint tableClass, uint reserved)
    {
        if (table == 0 || size < Table.Length) { size = (uint)Table.Length; return 122; }
        Marshal.Copy(Table, 0, table, Table.Length); size = (uint)Table.Length; return 0;
    }
    internal (long HighWater, int Matched, int Displayed) Run()
    {
        var selection = EndpointProcessSelection.Create(Identities, CancellationToken.None);
        var budget = new EndpointWorkBudget();
        int matched, displayed;
        using (var candidates = new EndpointCandidateSet(budget))
        {
            foreach (var identity in selection.Identities) candidates.AddProcess(identity.Pid);
            var status = IpHelperConnections.ReadNativeTable(Protocol, Read, pid => pid > 0 && pid <= Identities.Length,
                candidates.Add, budget, CancellationToken.None);
            if (status != IpHelperConnections.EndpointTableStatus.Ready) throw new Exception("Synthetic table was not read.");
            var result = candidates.Select(Alive, CancellationToken.None);
            matched = result.MatchedCount; displayed = result.Candidates.Length;
            var rows = new ProcessConnection[displayed];
            for (int i = 0; i < rows.Length; i++) rows[i] = IpHelperConnections.Materialize(result.Candidates[i], Identities[result.Candidates[i].Pid - 1]);
            if (displayed != (Exits ? 10 : 200) || matched != (Exits ? 10 : RowCount) ||
                rows.Any(x => !Alive.Contains(x.Process.Pid)) || budget.HighWaterBytes > EndpointWorkBudget.MaximumBytes)
                throw new Exception("Incorrect selection during benchmark.");
            GC.KeepAlive(rows);
        }
        if (budget.CurrentBytes != EndpointWorkBudget.MetadataAndOutputReserve) throw new Exception("Budget reservation was not released.");
        return (budget.HighWaterBytes, matched, displayed);
    }
    internal static Fixture Create(string name, int processes, int perProcess, EndpointProtocol protocol, bool reverse, bool exits = false)
    {
        int count = exits ? perProcess + 10 : processes * perProcess;
        int stride = protocol == EndpointProtocol.Tcp6 ? 56 : 12;
        var table = new byte[4 + count * stride];
        BinaryPrimitives.WriteInt32LittleEndian(table, count);
        for (int index = 0; index < count; index++)
        {
            int logical = reverse ? count - index - 1 : index;
            int pid = logical / perProcess + 1, endpoint = logical % perProcess;
            var row = table.AsSpan(4 + index * stride, stride);
            if (protocol == EndpointProtocol.Tcp6)
            {
                BinaryPrimitives.WriteUInt64BigEndian(row, 0xFE80000000000000);
                BinaryPrimitives.WriteUInt64BigEndian(row[8..], (ulong)(endpoint + 1));
                BinaryPrimitives.WriteUInt32BigEndian(row[16..], 3);
                BinaryPrimitives.WriteUInt16BigEndian(row[20..], (ushort)(endpoint + 1000));
                BinaryPrimitives.WriteUInt64BigEndian(row[24..], 0x20010DB800000000);
                BinaryPrimitives.WriteUInt64BigEndian(row[32..], 1);
                BinaryPrimitives.WriteUInt16BigEndian(row[44..], 443);
                BinaryPrimitives.WriteInt32LittleEndian(row[48..], 5);
                BinaryPrimitives.WriteInt32LittleEndian(row[52..], pid);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(row, 0x7F000001 + (uint)endpoint);
                BinaryPrimitives.WriteUInt16BigEndian(row[4..], (ushort)(endpoint + 1000));
                BinaryPrimitives.WriteInt32LittleEndian(row[8..], pid);
            }
        }
        return new() { Name = name, Table = table, Protocol = protocol, RowCount = count, Exits = exits,
            Identities = Enumerable.Range(1, processes).Select(pid => new ProcessIdentity(pid, pid + 1000)).ToArray(),
            Alive = (exits ? [2] : Enumerable.Range(1, processes)).ToHashSet() };
    }
}
