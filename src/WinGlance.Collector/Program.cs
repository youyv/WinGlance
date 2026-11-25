using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinGlance.Windows.Identity;
using WinGlance.Windows.Telemetry;

static string? Argument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

try
{
    var pipeName = Argument(args, "--pipe");
    int.TryParse(Argument(args, "--parent"), out var parentPid);
    long.TryParse(Argument(args, "--parent-start"), out var parentStart);
    int.TryParse(Argument(args, "--samples"), out var maxSamples);
    using var parent = parentPid != 0 ? ParentProcessLifetime.TryOpen(parentPid, parentStart) : null;
    if (parentPid != 0 && parent is null) return;
    using var resolver = new ProcessIdentityResolver();
    using var sampler = new WindowsSampler(resolver);
    using var pipe = pipeName is null ? null : new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
    if (pipe is not null) await pipe.ConnectAsync(15000);
    using var pipeWriter = pipe is null ? null : new StreamWriter(pipe) { AutoFlush = true };
    TextWriter output = pipeWriter ?? Console.Out;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    var count = 0;
    var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    do
    {
        if (parent is not null && !parent.IsAlive) break;
        var snapshot = sampler.Sample();
        await output.WriteLineAsync(JsonSerializer.Serialize(snapshot, jsonOptions));
        if (maxSamples > 0 && ++count >= maxSamples) break;
    } while (await timer.WaitForNextTickAsync());
}
catch (IOException) { /* The UI closed its pipe. Dispose the sampler and its own ETW session. */ }
catch (Exception ex)
{
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "collector-error.log"), $"{DateTimeOffset.Now:O}\n{ex}");
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
