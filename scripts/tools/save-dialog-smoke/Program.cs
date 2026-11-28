using System.Text.Json;
using WinGlance.App.Services;

if (args.Length < 2) throw new ArgumentException("save-dialog-smoke SUGGESTED_PATH RESULT_JSON [CANCEL_AFTER_MS] [--cancel-file PATH] [--no-diagnostics]");
using var cancellation = new CancellationTokenSource();
if (args.Length > 2 && int.TryParse(args[2], out var milliseconds))
{
    if (milliseconds <= 0) cancellation.Cancel(); else cancellation.CancelAfter(milliseconds);
}
using var finished = new CancellationTokenSource();
int cancelFileIndex = Array.IndexOf(args, "--cancel-file");
Task cancelWatcher = Task.CompletedTask;
if (cancelFileIndex >= 0 && cancelFileIndex + 1 < args.Length)
{
    string cancelFile = Path.GetFullPath(args[cancelFileIndex + 1]);
    cancelWatcher = Task.Run(async () =>
    {
        try
        {
            while (!finished.IsCancellationRequested)
            {
                if (File.Exists(cancelFile)) { cancellation.Cancel(); return; }
                await Task.Delay(50, finished.Token);
            }
        }
        catch (OperationCanceledException) { }
    });
}
object result;
try
{
    string? selected = await NativeSaveDialog.PickAsync(0, args[0], Path.GetExtension(args[0]).TrimStart('.'), cancellation.Token,
        args.Contains("--no-diagnostics") ? null : message => { lock (cancellation) File.AppendAllText(args[1] + ".trace", $"{DateTimeOffset.Now:O} {message}\n"); });
    result = new { Status = selected is null ? "UserCancelled" : "Selected", Path = selected, ExistsOnReturn = selected is not null && File.Exists(selected) };
}
catch (OperationCanceledException) { result = new { Status = "TokenCancelled" }; }
catch (Exception error) { result = new { Status = "Failed", Error = error.ToString() }; }
finished.Cancel();
await cancelWatcher;
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(result));
