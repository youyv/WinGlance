using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace WinGlance.Core;

[EventSource(Name = "WinGlance-Diagnostics")]
public sealed class DiagnosticTrace : EventSource
{
    public static readonly DiagnosticTrace Log = new();
    private DiagnosticTrace() { }
    [Event(1, Level = EventLevel.Informational)]
    public void Stage(string name, double milliseconds, long allocatedBytes, long count)
    { if (IsEnabled()) WriteEvent(1, name, milliseconds, allocatedBytes, count); }
    [Event(2, Level = EventLevel.Informational)]
    public void Flush(string phase, string reason, long id, double milliseconds)
    { if (IsEnabled()) WriteEvent(2, phase, reason, id, milliseconds); }
    [Event(3, Level = EventLevel.Informational)]
    public void StatisticsWork(string phase, string operation, long id, double milliseconds, long count, string errorType)
    { if (IsEnabled()) WriteEvent(3, phase, operation, id, milliseconds, count, errorType); }
    [NonEvent] public static long Start() => Log.IsEnabled() ? Stopwatch.GetTimestamp() : 0;
    [NonEvent] public static long Allocated() => Log.IsEnabled() ? GC.GetAllocatedBytesForCurrentThread() : 0;
    [NonEvent] public static void End(string name, long start, long allocated = 0, long count = 0)
    {
        if (start != 0) Log.Stage(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            allocated == 0 ? 0 : GC.GetAllocatedBytesForCurrentThread() - allocated, count);
    }
}
