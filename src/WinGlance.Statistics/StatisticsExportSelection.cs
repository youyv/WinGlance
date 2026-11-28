namespace WinGlance.Statistics;

public enum ExportOverwriteDecision { Cancel, Replace, Reselect }

// The picker returns only a path. Only an explicit decision grants replacement permission.
public static class StatisticsExportSelection
{
    public static Task<StatisticsExportDestination?> SelectAsync(
        Func<CancellationToken, Task<string?>> pickPath,
        Func<string, CancellationToken, Task<ExportOverwriteDecision>> confirmOverwrite,
        CancellationToken token) => SelectAsync(pickPath, confirmOverwrite, token, ExportFileSystem.Instance);

    internal static async Task<StatisticsExportDestination?> SelectAsync(
        Func<CancellationToken, Task<string?>> pickPath,
        Func<string, CancellationToken, Task<ExportOverwriteDecision>> confirmOverwrite,
        CancellationToken token, IExportFileSystem files)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string? selected = await pickPath(token);
            token.ThrowIfCancellationRequested();
            if (selected is null) return null;
            string path = Path.GetFullPath(selected);
            // Keep filesystem checks off the UI thread; cancellation never turns an
            // inaccessible/unknown target into a new-file decision.
            var state = await Task.Run(() => files.Probe(path), token);
            token.ThrowIfCancellationRequested();
            if (state == ExportPathState.Missing) return StatisticsExportDestination.CreateNew(path);
            var decision = await confirmOverwrite(path, token);
            token.ThrowIfCancellationRequested();
            if (decision == ExportOverwriteDecision.Replace) return StatisticsExportDestination.ConfirmedOverwrite(path);
            if (decision != ExportOverwriteDecision.Reselect) return null;
            // Reselect starts a new decision even if the user chooses the same path.
        }
    }
}
