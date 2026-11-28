using WinGlance.Statistics;

internal static class ExportSelectionTests
{
    internal static IEnumerable<(string Name, Func<string, Task> Run)> Cases =>
    [
        ("Picker-return race requires explicit confirmation and preserves declined content", BeforeProbeRace),
        ("Fixed new-file intent preserves a file created before commit", AfterProbeRace),
        ("Reselect never transfers overwrite permission to another or repeated path", Reselect),
        ("Explicit replacement commits and missing replacement never becomes creation", Replace),
        ("Cancellation wins over an affirmative overwrite response", Cancel),
        ("Unknown, inaccessible and directory targets fail without granting permission", UnknownTarget)
    ];

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Target(string fixture, string name = "selected.json") => Path.Combine(Path.GetDirectoryName(fixture)!, name);

    private static async Task BeforeProbeRace(string fixture)
    {
        string path = Target(fixture);
        int confirmations = 0;
        var selected = await StatisticsExportSelection.SelectAsync(_ =>
        {
            Require(!File.Exists(path), "Selection should start with no target.");
            // A different writer acts after path selection and before the UI continuation.
            File.WriteAllText(path, "independent writer");
            return Task.FromResult<string?>(path);
        }, (candidate, _) =>
        {
            confirmations++;
            Require(candidate == path, "Consent must name the actual final path.");
            return Task.FromResult(ExportOverwriteDecision.Cancel);
        }, CancellationToken.None);
        Require(selected is null && confirmations == 1 && File.ReadAllText(path) == "independent writer",
            "The picker-return race silently granted replacement permission.");
    }

    private static async Task AfterProbeRace(string fixture)
    {
        string path = Target(fixture), temporary = Target(fixture, "owned.tmp");
        var selected = await StatisticsExportSelection.SelectAsync(_ => Task.FromResult<string?>(path),
            (_, _) => throw new InvalidOperationException("No confirmation is needed for a missing target."), CancellationToken.None);
        Require(selected?.Intent == ExportWriteIntent.CreateNew, "New-file intent was not fixed.");
        File.WriteAllText(path, "independent writer"); File.WriteAllText(temporary, "export");
        var result = StatisticsExportCommitter.Commit(temporary, selected!, ExportFileSystem.Instance);
        Require(result.Phase == StatisticsExportPhase.Failed && File.ReadAllText(path) == "independent writer",
            "A later file converted new-file intent into replacement.");
    }

    private static async Task Reselect(string fixture)
    {
        string existing = Target(fixture), fresh = Target(fixture, "new.json");
        File.WriteAllText(existing, "keep");
        int picks = 0, confirmations = 0;
        var selected = await StatisticsExportSelection.SelectAsync(_ => Task.FromResult<string?>(++picks == 1 ? existing : fresh),
            (_, _) => { confirmations++; return Task.FromResult(ExportOverwriteDecision.Reselect); }, CancellationToken.None);
        Require(selected?.Path == fresh && selected.Intent == ExportWriteIntent.CreateNew && confirmations == 1 && File.ReadAllText(existing) == "keep",
            "Reselect leaked consent to a new target.");

        picks = confirmations = 0;
        selected = await StatisticsExportSelection.SelectAsync(_ => { picks++; return Task.FromResult<string?>(existing); },
            (_, _) => Task.FromResult(++confirmations == 1 ? ExportOverwriteDecision.Reselect : ExportOverwriteDecision.Cancel), CancellationToken.None);
        Require(selected is null && picks == 2 && confirmations == 2 && File.ReadAllText(existing) == "keep",
            "The same path was not confirmed again after reselecting.");

        picks = confirmations = 0;
        selected = await StatisticsExportSelection.SelectAsync(_ => Task.FromResult<string?>(existing), (_, _) =>
        {
            confirmations++; File.Delete(existing);
            return Task.FromResult(ExportOverwriteDecision.Reselect);
        }, CancellationToken.None);
        Require(selected?.Intent == ExportWriteIntent.CreateNew && confirmations == 1,
            "A disappeared target retained old overwrite permission.");
        File.WriteAllText(existing, "recreated"); File.WriteAllText(Target(fixture, "recreated.tmp"), "export");
        Require(StatisticsExportCommitter.Commit(Target(fixture, "recreated.tmp"), selected!, ExportFileSystem.Instance).Phase == StatisticsExportPhase.Failed &&
            File.ReadAllText(existing) == "recreated", "Delete/recreate race lost the new writer's content.");
    }

    private static async Task Replace(string fixture)
    {
        string path = Target(fixture), temporary = Target(fixture, "owned.tmp");
        File.WriteAllText(path, "original"); File.WriteAllText(temporary, "export");
        var selected = await StatisticsExportSelection.SelectAsync(_ => Task.FromResult<string?>(path),
            (_, _) => Task.FromResult(ExportOverwriteDecision.Replace), CancellationToken.None);
        Require(selected?.Intent == ExportWriteIntent.ConfirmedOverwrite, "Explicit consent was lost.");
        var result = StatisticsExportCommitter.Commit(temporary, selected!, ExportFileSystem.Instance);
        Require(result.Phase == StatisticsExportPhase.Completed && File.ReadAllText(path) == "export" &&
            !Directory.EnumerateDirectories(Path.GetDirectoryName(path)!, ".winglance-recovery-*").Any(), "Confirmed replacement failed.");
        File.Delete(path); File.WriteAllText(temporary, "second export");
        result = StatisticsExportCommitter.Commit(temporary, selected!, ExportFileSystem.Instance);
        Require(result.Phase == StatisticsExportPhase.Failed && !File.Exists(path), "Missing confirmed target silently became a new file.");
    }

    private static async Task Cancel(string fixture)
    {
        string path = Target(fixture); File.WriteAllText(path, "keep");
        using var cancel = new CancellationTokenSource();
        try
        {
            await StatisticsExportSelection.SelectAsync(_ => Task.FromResult<string?>(path), (_, _) =>
            {
                cancel.Cancel(); return Task.FromResult(ExportOverwriteDecision.Replace);
            }, cancel.Token);
            throw new InvalidOperationException("Cancelled affirmative result granted permission.");
        }
        catch (OperationCanceledException) { }
        Require(File.ReadAllText(path) == "keep", "Cancelled selection touched the target.");
        bool picked = false;
        try
        {
            await StatisticsExportSelection.SelectAsync(_ => { picked = true; return Task.FromResult<string?>(path); },
                (_, _) => Task.FromResult(ExportOverwriteDecision.Replace), cancel.Token);
            throw new InvalidOperationException("Precancelled selection continued.");
        }
        catch (OperationCanceledException) { }
        Require(!picked, "Precancelled selection opened the picker.");
    }

    private static async Task UnknownTarget(string fixture)
    {
        foreach (string path in new[] { Path.GetDirectoryName(fixture)!, Target(fixture, "missing-parent/child.json") })
        {
            try
            {
                await StatisticsExportSelection.SelectAsync(_ => Task.FromResult<string?>(path),
                    (_, _) => throw new InvalidOperationException("Unknown target reached confirmation."), CancellationToken.None);
                throw new InvalidOperationException("Invalid target was accepted.");
            }
            catch (IOException) { }
        }
        try
        {
            await StatisticsExportSelection.SelectAsync(_ => Task.FromResult<string?>(Target(fixture)),
                (_, _) => throw new InvalidOperationException("Unknown target reached confirmation."), CancellationToken.None, new DeniedFiles());
            throw new InvalidOperationException("Access denial became new-file intent.");
        }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class DeniedFiles : IExportFileSystem
    {
        public ExportPathState Probe(string path) => throw new UnauthorizedAccessException("Controlled denial");
        public string CreateRecoveryDirectory(string parent) => throw new NotSupportedException();
        public void Move(string source, string destination) => throw new NotSupportedException();
        public void Replace(string source, string destination, string backup) => throw new NotSupportedException();
        public void DeleteFile(string path) => throw new NotSupportedException();
        public void DeleteDirectory(string path) => throw new NotSupportedException();
    }
}
