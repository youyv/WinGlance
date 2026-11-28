using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinGlance.Core;

internal static class DiagnosticReplay
{
    // This deterministic peer is explicitly opted into by the isolated diagnostics launcher.
    // Its first application owns real loopback endpoints so detail queries remain native.
    internal static async Task Run(string pipeName, string directory, Func<bool> parentAlive)
    {
        Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var sockets = new List<UdpClient>();
        var run = Guid.NewGuid();
        long sequence = 0;
        try
        {
            for (int i = 0; i < 40; i++) sockets.Add(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)));
            do
            {
                if (!parentAlive()) break;
                if (File.Exists(Path.Combine(directory, "stall"))) continue;
                var end = DateTimeOffset.UtcNow;
                var start = end.AddSeconds(-1);
                var rows = new List<ProcessSample>();
                var accounting = new List<ProcessAccounting>();
                bool exitSecondApplication = File.Exists(Path.Combine(directory, "exit001"));
                for (int i = 0; i < 100; i++)
                {
                    if (i == 1 && exitSecondApplication) continue;
                    var identity = i == 0 ? new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks) : new(100000 + i, 1000 + i);
                    var software = new SoftwareIdentity("diagnostic:" + i, $"诊断应用 {i:000}", $"fixture{i:000}.exe",
                        $"C:\\WinGlanceFixture\\{i:000}.exe", "WinGlance diagnostic fixture", SoftwareCategory.Application, "固定诊断输入");
                    double cpu = i == 0 ? 9 : ((i + sequence) % 9) / 10d;
                    long memory = (40 + i) * 1048576L;
                    rows.Add(new(identity, software, cpu, memory, 1000 + i, 100 + i, 400 + i, 200 + i,
                        new Dictionary<string, double> { ["engine0"] = i % 3 }, 1048576L * i, 0));
                    accounting.Add(new(identity, software, 1, Metrics(cpu, memory, 1000 + i, 100 + i, start, end), ObservedStartUtc: start, ObservedEndUtc: end));
                }
                var snapshot = new MonitorSnapshot(end, 1, new(18, 17179869184, 8589934592, 1048576, 131072, 2097152, 524288, 12, "诊断回放"),
                    rows, new Dictionary<string, Capability> { ["network"] = new(MetricState.Ready, "固定诊断输入"), ["gpu"] = new(MetricState.Ready, "固定诊断输入") }, false)
                { Accounting = new(run, sequence++, start, end, 1, accounting,
                    new SystemAccounting(Metrics(18, 8589934592, 1048576, 131072, start, end), NetworkScope: "diagnostic",
                        TotalMemoryBytes: 17179869184, DedicatedGpuCapacityBytes: 8589934592)) };
                // Isolated visual verification only: accounting remains the deterministic
                // fixture above; this flag exercises display boundaries, never user data.
                if (File.Exists(Path.Combine(directory, "large-overlay")))
                {
                    double rate = 1023.9 * Math.Pow(1024, 4);
                    snapshot = snapshot with { System = snapshot.System with { CpuPercent = 100, GpuPercent = 100,
                        DownloadBytesPerSecond = rate, UploadBytesPerSecond = rate,
                        DiskReadBytesPerSecond = rate, DiskWriteBytesPerSecond = rate } };
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot));
            } while (await timer.WaitForNextTickAsync());
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    private static IReadOnlyDictionary<StatisticMetric, MetricAccounting> Metrics(double cpu, long memory, long down, long up, DateTimeOffset start, DateTimeOffset end) =>
        new Dictionary<StatisticMetric, MetricAccounting>
        {
            [StatisticMetric.Cpu] = new(cpu, 1, cpu, CpuTicksDelta: (long)(cpu * TimeSpan.TicksPerSecond / 100), StartUtc: start, EndUtc: end),
            [StatisticMetric.Gpu] = new(12, 1, 12, StartUtc: start, EndUtc: end),
            [StatisticMetric.Memory] = new(memory, 1, memory, StartUtc: start, EndUtc: end),
            [StatisticMetric.Download] = new(down, 1, down, BytesDelta: down, StartUtc: start, EndUtc: end),
            [StatisticMetric.Upload] = new(up, 1, up, BytesDelta: up, StartUtc: start, EndUtc: end),
            [StatisticMetric.DiskRead] = new(2097152, 1, 2097152, BytesDelta: 2097152, StartUtc: start, EndUtc: end),
            [StatisticMetric.DiskWrite] = new(524288, 1, 524288, BytesDelta: 524288, StartUtc: start, EndUtc: end)
        };
}
