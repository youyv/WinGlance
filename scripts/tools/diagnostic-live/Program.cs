using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

string Option(string name, string fallback = "")
{ int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
int Number(string name, int fallback) => int.Parse(Option(name, fallback.ToString()));
if (args.FirstOrDefault() == "attach")
{
    int pid = Number("--pid", 0), minimum = Number("--min-seconds", 90), maximum = Number("--max-seconds", 180), needed = Number("--flushes", 3);
    string file = Path.GetFullPath(Option("--trace"));
    using var capture = new LiveCapture(pid, file);
    capture.Start();
    var elapsed = Stopwatch.StartNew();
    while (elapsed.Elapsed.TotalSeconds < maximum &&
        (elapsed.Elapsed.TotalSeconds < minimum || capture.PeriodicFlushes < needed) && !capture.Completed)
        await Task.Delay(250);
    bool sufficient = elapsed.Elapsed.TotalSeconds >= minimum && capture.PeriodicFlushes >= needed;
    await capture.Stop();
    await JsonFile(Path.ChangeExtension(file, ".capture.json"), new
    {
        ProcessId = pid, DurationSeconds = elapsed.Elapsed.TotalSeconds, MinimumSeconds = minimum,
        MaximumSeconds = maximum, RequiredPeriodicFlushes = needed, CompletedPeriodicFlushes = capture.PeriodicFlushes,
        Complete = sufficient, Trace = file, Events = capture.FlushEvents, capture.Error
    });
    Console.WriteLine($"Completed {capture.PeriodicFlushes} periodic flushes in {elapsed.Elapsed.TotalSeconds:F1}s; sufficient={sufficient}");
    return sufficient && capture.Error is null ? 0 : 1;
}
if (args.FirstOrDefault() != "collector")
{
    Console.Error.WriteLine("attach --pid PID --trace file --min-seconds 90 --max-seconds 180 --flushes 3\ncollector --exe frozen/WinGlance.Collector.exe --output new-directory --rounds 5 --frames 60");
    return 2;
}

string executable = Path.GetFullPath(Option("--exe")), output = Path.GetFullPath(Option("--output"));
if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
Directory.CreateDirectory(output);
int rounds = Number("--rounds", 5), frames = Number("--frames", 60);
bool traceEnabled = !args.Contains("--no-trace");
using var identity = WindowsIdentity.GetCurrent();
bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
using var owner = Process.GetCurrentProcess();
var roundResults = new List<object>();
using var workload = new FixedNetworkWorkload();
await JsonFile(Path.Combine(output, "environment.json"), new
{
    StartedUtc = DateTimeOffset.UtcNow, Executable = executable,
    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(executable))),
    Elevated = elevated, LogicalProcessors = Environment.ProcessorCount, Rounds = rounds, Frames = frames,
    Workload = "One fixed harness process with 16 UDP loopback endpoints; one 32768-byte datagram every 125ms (256 KiB/s) and receiver drain. Other host processes remain uncontrolled and their observed counts are recorded.",
    TraceEnabled = traceEnabled,
    Trace = traceEnabled ? "WinGlance-Diagnostics Informational; .NET GC Verbose allocation ticks; SampleProfiler; all rounds use identical providers." : "No EventPipe or diagnostic provider enabled."
});
for (int round = 1; round <= rounds; round++)
{
    string stem = Path.Combine(output, $"collector-{round:00}"), pipeName = "WinGlance.DiagnosticMeasure." + Guid.NewGuid().ToString("N");
    string collectorData = stem + "-data";
    Directory.CreateDirectory(collectorData);
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (var arg in new[] { "--pipe", pipeName, "--parent", owner.Id.ToString(), "--parent-start", owner.StartTime.ToUniversalTime().Ticks.ToString(), "--samples", (frames + 5).ToString() }) info.ArgumentList.Add(arg);
    info.Environment.Remove("WINGLANCE_DIAGNOSTIC_REPLAY");
    info.Environment["WINGLANCE_DIAGNOSTICS_DIR"] = collectorData;
    using var process = Process.Start(info)!;
    long startTicks = process.StartTime.ToUniversalTime().Ticks;
    var stderr = process.StandardError.ReadToEndAsync();
    LiveCapture? capture = null;
    var observations = new List<object>();
    var watch = Stopwatch.StartNew();
    try
    {
        // The pipe does not exist until tracing is attached, so the production collector
        // cannot begin Sample() before the first captured stage.
        for (int retry = 0; traceEnabled; retry++)
        {
            try { capture = new(process.Id, stem + ".nettrace"); capture.Start(); break; }
            catch when (retry < 25 && !process.HasExited) { await Task.Delay(100); }
        }
        using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var reader = new StreamReader(pipe);
            for (int frame = 1; frame <= frames; frame++)
            {
                string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) ?? throw new EndOfStreamException("Collector stopped before all frames.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                process.Refresh();
                var snapshot = new
                {
                    Frame = frame, ElapsedSeconds = watch.Elapsed.TotalSeconds,
                    IsElevated = root.GetProperty("IsElevated").GetBoolean(), Processes = root.GetProperty("Processes").GetArrayLength(),
                    JsonBytes = System.Text.Encoding.UTF8.GetByteCount(line), CpuSeconds = process.TotalProcessorTime.TotalSeconds,
                    PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64, KernelHandles = process.HandleCount,
                    Capabilities = root.GetProperty("Capabilities").Clone(),
                    EtwQuality = root.TryGetProperty("Accounting", out var accounting) && accounting.TryGetProperty("System", out var system) &&
                        system.TryGetProperty("EtwQuality", out var quality) ? quality.Clone() : (JsonElement?)null
                };
                observations.Add(snapshot);
                if (snapshot.IsElevated != elevated) throw new InvalidOperationException("Collector elevation does not match its benchmark harness.");
            }
            // Stop trace while the child is still alive so rundown can resolve managed names.
            if (capture is not null) await capture.Stop();
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string errors = await stderr;
        await File.WriteAllTextAsync(stem + ".stderr.txt", errors);
        var result = new { Round = round, Elevated = elevated, ProcessId = process.Id, StartTicks = startTicks,
            DurationSeconds = watch.Elapsed.TotalSeconds, Frames = observations.Count, ExitCode = process.ExitCode,
            TraceEnabled = traceEnabled, TraceError = capture?.Error, Observations = observations };
        roundResults.Add(result); await JsonFile(stem + ".json", result);
        Console.WriteLine($"Round {round}/{rounds}: {observations.Count} frames, PID {process.Id}, elevated={elevated}");
    }
    finally
    {
        capture?.Dispose();
        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks) process.Kill();
    }
}
await JsonFile(Path.Combine(output, "rounds.json"), roundResults);
return 0;

static Task JsonFile(string path, object value) => File.WriteAllTextAsync(path,
    JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

sealed class LiveCapture : IDisposable
{
    private readonly EventPipeSession _session;
    private readonly FileStream _file;
    private readonly EventPipeEventSource _source;
    private Task? _processing;
    private int _periodic;
    private bool _stopped;
    public int PeriodicFlushes => Volatile.Read(ref _periodic);
    public bool Completed => _processing?.IsCompleted == true;
    public string? Error { get; private set; }
    public List<object> FlushEvents { get; } = new();
    public LiveCapture(int pid, string trace)
    {
        _session = new DiagnosticsClient(pid).StartEventPipeSession(new[]
        {
            new EventPipeProvider("WinGlance-Diagnostics", EventLevel.Informational, long.MaxValue),
            new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x1),
            new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational)
        }, requestRundown: true, circularBufferMB: 32);
        _file = new FileStream(trace, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _source = new EventPipeEventSource(new RecordingStream(_session.EventStream, _file));
        _source.Dynamic.All += data =>
        {
            if (data.ProviderName != "WinGlance-Diagnostics" || (int)data.ID != 2) return;
            string phase = Convert.ToString(data.PayloadByName("phase")) ?? "", reason = Convert.ToString(data.PayloadByName("reason")) ?? "";
            FlushEvents.Add(new { AtMilliseconds = data.TimeStampRelativeMSec, Phase = phase, Reason = reason,
                Id = Convert.ToInt64(data.PayloadByName("id")), Milliseconds = Convert.ToDouble(data.PayloadByName("milliseconds")) });
            if (phase == "finished" && reason == "periodic") Interlocked.Increment(ref _periodic);
        };
    }
    public void Start() => _processing = Task.Run(() =>
    {
        try { _source.Process(); }
        catch (Exception error) { Error = error.ToString(); }
    });
    public async Task Stop()
    {
        if (_stopped) return; _stopped = true;
        try { _session.Stop(); }
        catch (ServerNotAvailableException) { }
        if (_processing is not null) await _processing.WaitAsync(TimeSpan.FromSeconds(25));
        await _file.FlushAsync();
    }
    public void Dispose()
    {
        if (!_stopped) { try { _session.Stop(); } catch { } }
        _source.Dispose(); _session.Dispose(); _file.Dispose();
    }
}

sealed class RecordingStream(Stream source, Stream destination) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { int read = source.Read(buffer, offset, count); destination.Write(buffer, offset, read); return read; }
    public override int Read(Span<byte> buffer)
    { int read = source.Read(buffer); destination.Write(buffer[..read]); return read; }
    public override void Flush() => destination.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class FixedNetworkWorkload : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<UdpClient> _endpoints = new();
    private readonly Task _sending, _receiving;
    public FixedNetworkWorkload()
    {
        for (int i = 0; i < 16; i++) _endpoints.Add(new(new IPEndPoint(IPAddress.Loopback, 0)));
        var receiver = _endpoints[0]; var sender = _endpoints[1];
        sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        _sending = Task.Run(async () =>
        {
            var bytes = new byte[32768]; using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(125));
            try { while (await timer.WaitForNextTickAsync(_stop.Token)) await sender.SendAsync(bytes, _stop.Token); }
            catch (OperationCanceledException) { }
        });
        _receiving = Task.Run(async () =>
        {
            try { while (!_stop.IsCancellationRequested) await receiver.ReceiveAsync(_stop.Token); }
            catch (OperationCanceledException) { }
        });
    }
    public void Dispose()
    {
        _stop.Cancel(); Task.WhenAll(_sending, _receiving).GetAwaiter().GetResult();
        foreach (var endpoint in _endpoints) endpoint.Dispose(); _stop.Dispose();
    }
}
