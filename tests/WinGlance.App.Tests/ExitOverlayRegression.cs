using System.Collections.Concurrent;
using System.Diagnostics;
using WinGlance.App.Services;

internal static class ExitOverlayRegression
{
    internal static async Task Run(Action<bool, string> check)
    {
        check(OverlayPlacement.SizeForScale(1) == (510, 58) &&
              OverlayPlacement.SizeForScale(1.5) == (765, 87) &&
              OverlayPlacement.SizeForScale(2) == (1020, 116),
            "overlay physical size preserves 510 x 58 DIPs at 100%, 150% and 200%");
        check(OverlayPlacement.SizeForScale(double.NaN) == (510, 58) &&
              OverlayPlacement.SizeForScale(0) == (510, 58) &&
              OverlayPlacement.SizeForScale(double.PositiveInfinity) == (510, 58),
            "invalid overlay scale falls back to 96 DPI");
        var large = OverlayPlacement.SizeForScale(2);
        check(OverlayPlacement.Recover(-1700, 1000, large.Width, large.Height,
                [new(-1920, 0, 1920, 1080)]) == (-1700, 964),
            "scaled overlay stays within a negative-coordinate monitor work area");

        var messages = new ConcurrentQueue<string>();
        var pendingSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Use a deliberately slow test-only UI preparation callback. The coordinator must
        // not wait another full timeout after this callback has already used the budget.
        var slow = new SessionEndCoordinator(() => Thread.Sleep(220), () => Task.CompletedTask,
            () => pendingSave.Task, messages.Enqueue);
        var elapsed = Stopwatch.StartNew();
        slow.End(true, TimeSpan.FromMilliseconds(150));
        check(slow.PreparationElapsed >= TimeSpan.FromMilliseconds(150) &&
              slow.LastEndElapsed >= slow.PreparationElapsed &&
              messages.Any(x => x.Contains("同步准备")),
            "session end includes synchronous preparation in measured timeout and reports overrun");
        check(slow.LastEndElapsed - slow.PreparationElapsed < TimeSpan.FromMilliseconds(125) &&
              elapsed.Elapsed < TimeSpan.FromSeconds(1) && !pendingSave.Task.IsCompleted,
            "session end does not block on pending save after preparation exhausted its budget");
        pendingSave.SetResult();
        await slow.FinishAsync();

        int attempts = 0, saves = 0;
        messages.Clear();
        var failedStop = new SessionEndCoordinator(() => { attempts++; throw new IOException("stop-fixture"); },
            () => Task.CompletedTask, () => { saves++; return Task.CompletedTask; }, messages.Enqueue);
        await failedStop.FinishAsync();
        await failedStop.FinishAsync();
        check(attempts == 1 && saves == 1 && messages.Any(x => x.Contains("stop-fixture")),
            "preparation failure is logged and final save still runs once");
    }
}
