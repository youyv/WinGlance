using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: diagnostic-reader input.nettrace [output.json]");
    return 2;
}

string input = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.ChangeExtension(input, ".diagnostics.json"));
bool includeStacks = args.Contains("--stacks");
var stages = new List<StageEvent>();
var flushes = new List<FlushEvent>();
var work = new List<WorkEvent>();
var allocation = new Dictionary<(int Process, int Thread, string Type), AllocationTotal>();
var eventErrors = new List<string>();
long allocationEvents = 0, estimatedAllocatedBytes = 0, allEvents = 0;
double firstMs = double.PositiveInfinity, lastMs = 0;
using (var source = new EventPipeEventSource(input))
{
    source.AllEvents += data =>
    {
        allEvents++; firstMs = Math.Min(firstMs, data.TimeStampRelativeMSec); lastMs = Math.Max(lastMs, data.TimeStampRelativeMSec);
    };
    source.Dynamic.All += data =>
    {
        if (data.ProviderName != "WinGlance-Diagnostics") return;
        string Text(string name) => Convert.ToString(data.PayloadByName(name), CultureInfo.InvariantCulture) ?? "";
        double Number(string name) => Convert.ToDouble(data.PayloadByName(name), CultureInfo.InvariantCulture);
        long Integer(string name) => Convert.ToInt64(data.PayloadByName(name), CultureInfo.InvariantCulture);
        switch ((int)data.ID)
        {
            case 0: eventErrors.Add(data.ToString()); break;
            case 1: stages.Add(new(data.TimeStampRelativeMSec, data.ProcessID, data.ThreadID, Text("name"),
                Number("milliseconds"), Integer("allocatedBytes"), Integer("count"))); break;
            case 2: flushes.Add(new(data.TimeStampRelativeMSec, data.ProcessID, data.ThreadID, Text("phase"),
                Text("reason"), Integer("id"), Number("milliseconds"))); break;
            case 3: work.Add(new(data.TimeStampRelativeMSec, data.ProcessID, data.ThreadID, Text("phase"),
                Text("operation"), Integer("id"), Number("milliseconds"), Integer("count"), Text("errorType"))); break;
        }
    };
    source.Clr.GCAllocationTick += data =>
    {
        long amount = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount;
        var key = (data.ProcessID, data.ThreadID, data.TypeName ?? "<unknown>");
        allocation.TryGetValue(key, out var previous);
        allocation[key] = new((previous?.Samples ?? 0) + 1, (previous?.SampledBytes ?? 0) + amount);
        allocationEvents++; estimatedAllocatedBytes += amount;
    };
    source.Process();
}

object? cpuStacks = null;
if (includeStacks)
{
    string etlx = TraceLog.CreateFromEventPipeDataFile(input);
    using var log = new TraceLog(etlx);
    var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
    var exclusive = new Dictionary<string, long>(StringComparer.Ordinal);
    var byThread = new Dictionary<int, long>();
    long samples = 0, managedSamples = 0, externalSamples = 0, errorSamples = 0, missingStacks = 0, withoutManagedSymbol = 0;
    foreach (var data in log.Events)
    {
        if (data is not ClrThreadSampleTraceData sample) continue;
        samples++; byThread[data.ThreadID] = byThread.GetValueOrDefault(data.ThreadID) + 1;
        // External samples often contain a managed waiter under native blocking. Counting
        // these as CPU hotspots would falsely attribute most runtime cost to idle waits.
        if (sample.Type == ClrThreadSampleType.External) { externalSamples++; continue; }
        if (sample.Type != ClrThreadSampleType.Managed) { errorSamples++; continue; }
        managedSamples++;
        var stack = data.CallStack();
        if (stack is null) { missingStacks++; continue; }
        bool first = true, managed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; stack is not null && depth < 512; depth++, stack = stack.Caller)
        {
            string name = stack.CodeAddress.FullMethodName;
            if (!string.IsNullOrEmpty(name)) managed = true;
            else name = $"{stack.CodeAddress.ModuleFile?.Name ?? "<unresolved>"}!0x{stack.CodeAddress.Address:x}";
            if (first) { exclusive[name] = exclusive.GetValueOrDefault(name) + 1; first = false; }
            if (seen.Add(name)) inclusive[name] = inclusive.GetValueOrDefault(name) + 1;
        }
        if (!managed) withoutManagedSymbol++;
    }
    cpuStacks = new
    {
        Samples = samples, ManagedSamples = managedSamples, ExternalSamples = externalSamples, ErrorSamples = errorSamples,
        MissingManagedStacks = missingStacks, ManagedStacksWithoutSymbols = withoutManagedSymbol,
        Caveat = "Hotspots include only SampleProfiler Managed samples. External samples can include blocked waits and native execution and are excluded, not called CPU cost. These are samples, not measured CPU milliseconds. Native method symbols/render/compositor attribution needs ETW and symbols. Inclusive counts overlap.",
        Inclusive = inclusive.OrderByDescending(p => p.Value).Take(120).Select(p => new { Method = p.Key, Samples = p.Value }).ToArray(),
        Exclusive = exclusive.OrderByDescending(p => p.Value).Take(120).Select(p => new { Method = p.Key, Samples = p.Value }).ToArray(),
        Threads = byThread.OrderByDescending(p => p.Value).Select(p => new { ThreadId = p.Key, Samples = p.Value }).ToArray()
    };
}

var summary = new
{
    Input = input,
    ReadUtc = DateTimeOffset.UtcNow,
    DurationSeconds = double.IsFinite(firstMs) ? (lastMs - firstMs) / 1000 : 0,
    AllEvents = allEvents,
    CpuStacks = cpuStacks,
    EventSourceErrors = eventErrors,
    Notes = new[]
    {
        "Stage timings are elapsed synchronous wall time, not CPU time; nested stages must not be summed as independent cost.",
        "StatisticsWork start.Milliseconds measures queue latency; finished.Milliseconds measures worker execution. Flush finished.Milliseconds is total actual flush time.",
        "Flush phase durations are individual stages; queued/start do not count as completed flushes. Exit flush is direct and has no queued event.",
        "Allocation samples are runtime sampling estimates attributed to the triggering sample's type; they are not exact per-type allocation totals or retained memory.",
        "Thread IDs are reported without inferring that a non-UI thread is a render thread. Trace boundaries may truncate individual operation lifecycles."
    },
    StageSummary = stages.GroupBy(e => new { e.ProcessId, e.Name }).Select(group => new
    {
        group.Key.ProcessId, group.Key.Name, Samples = group.Count(),
        Time = Summarize(group.Select(e => e.Milliseconds)),
        AllocatedBytes = group.Sum(e => e.AllocatedBytes), Threads = group.Select(e => e.ThreadId).Distinct().Order().ToArray()
    }).OrderBy(e => e.ProcessId).ThenByDescending(e => e.Time.Total).ToArray(),
    FlushSummary = flushes.GroupBy(e => new { e.ProcessId, e.Reason, e.Phase }).Select(group => new
    {
        group.Key.ProcessId, group.Key.Reason, group.Key.Phase, Samples = group.Count(),
        Time = Summarize(group.Select(e => e.Milliseconds))
    }).ToArray(),
    FlushOperations = flushes.GroupBy(e => new { e.ProcessId, e.Id }).Select(group => new
    {
        group.Key.ProcessId, group.Key.Id, Reason = group.First().Reason,
        Started = group.Any(e => e.Phase == "start"), Finished = group.Any(e => e.Phase == "finished"),
        Failed = group.Any(e => e.Phase == "failed"), Events = group.ToArray()
    }).ToArray(),
    WorkSummary = work.GroupBy(e => new { e.ProcessId, e.Operation, e.Phase }).Select(group => new
    {
        group.Key.ProcessId, group.Key.Operation, group.Key.Phase, Samples = group.Count(),
        Time = Summarize(group.Select(e => e.Milliseconds)), Errors = group.Select(e => e.ErrorType).Where(e => e.Length > 0).Distinct().ToArray()
    }).ToArray(),
    AllocationSamples = new
    {
        Samples = allocationEvents, EstimatedBytes = estimatedAllocatedBytes,
        ByType = allocation.GroupBy(e => new { e.Key.Process, e.Key.Type }).Select(group => new
        {
            group.Key.Process, group.Key.Type, Samples = group.Sum(e => e.Value.Samples),
            SampledBytes = group.Sum(e => e.Value.SampledBytes)
        }).OrderByDescending(e => e.SampledBytes).Take(100).ToArray(),
        ByThread = allocation.GroupBy(e => new { e.Key.Process, e.Key.Thread }).Select(group => new
        {
            group.Key.Process, group.Key.Thread, Samples = group.Sum(e => e.Value.Samples),
            SampledBytes = group.Sum(e => e.Value.SampledBytes)
        }).OrderByDescending(e => e.SampledBytes).ToArray()
    },
    Stages = stages,
    WorkEvents = work
};
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Read {allEvents} events: {stages.Count} stages, {flushes.Count} flush events, {work.Count} work events, {allocationEvents} allocation samples.");
Console.WriteLine(output);
return eventErrors.Count == 0 ? 0 : 1;

static TimingSummary Summarize(IEnumerable<double> values)
{
    double[] sorted = values.Order().ToArray();
    if (sorted.Length == 0) return new(0, 0, 0, 0, 0);
    double Quantile(double fraction)
    {
        double index = (sorted.Length - 1) * fraction;
        int lower = (int)Math.Floor(index), upper = (int)Math.Ceiling(index);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
    }
    return new(sorted.Sum(), sorted.Average(), Quantile(.5), Quantile(.95), sorted[^1]);
}

sealed record TimingSummary(double Total, double Mean, double Median, double P95, double Max);
sealed record StageEvent(double AtMilliseconds, int ProcessId, int ThreadId, string Name, double Milliseconds, long AllocatedBytes, long Count);
sealed record FlushEvent(double AtMilliseconds, int ProcessId, int ThreadId, string Phase, string Reason, long Id, double Milliseconds);
sealed record WorkEvent(double AtMilliseconds, int ProcessId, int ThreadId, string Phase, string Operation, long Id, double Milliseconds, long Count, string ErrorType);
sealed record AllocationTotal(long Samples, long SampledBytes);
