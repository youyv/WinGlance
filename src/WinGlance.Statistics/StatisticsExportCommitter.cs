namespace WinGlance.Statistics;

internal static class StatisticsExportCommitter
{
    internal static StatisticsExportResult Commit(string temporary, StatisticsExportDestination destination, IExportFileSystem files)
    {
        string? directory = null, backup = null;
        try
        {
            if (destination.Intent == ExportWriteIntent.CreateNew)
            {
                files.Move(temporary, destination.Path);
                return new(StatisticsExportPhase.Completed, destination.Path);
            }
            directory = files.CreateRecoveryDirectory(Path.GetDirectoryName(destination.Path)!);
            backup = Path.Combine(directory, "original.bak");
            files.Replace(temporary, destination.Path, backup);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never delete a possible original backup, nor replace a destination that appeared during recovery.
            bool backupKnown = TryProbe(files, backup, out var backupState);
            bool destinationKnown = TryProbe(files, destination.Path, out var destinationState);
            int code = Win32Code(error);
            if (code == 1177 && backupKnown && backupState == ExportPathState.File)
            {
                try
                {
                    files.Move(backup!, destination.Path);
                    string? cleanup = CleanupOwnedTemporary(temporary, files);
                    string? directoryWarning = CleanupDirectory(directory, files);
                    return new(StatisticsExportPhase.Failed, RecoveryPath: cleanup is null ? directoryWarning is null ? null : directory : temporary,
                        Error: "导出替换失败；原文件已恢复。" + error.Message,
                        Warning: Join(cleanup, directoryWarning));
                }
                catch (Exception restoreError) when (restoreError is not OutOfMemoryException)
                {
                    return new(StatisticsExportPhase.Failed, RecoveryPath: backup,
                        Error: "导出替换失败，原文件恢复未确认；请保留恢复备份。" + error.Message + "；" + restoreError.Message);
                }
            }
            if (code == 1177)
            {
                // This error promises an original at B. If B cannot be confirmed, a concurrent
                // D is not evidence that our original survived; retain all remaining owned files.
                return new(StatisticsExportPhase.Failed, RecoveryPath: backup ?? directory ?? temporary,
                    Error: "导出替换失败，原文件恢复位置未能确认；已保留临时文件与恢复目录。" + error.Message);
            }
            if (!backupKnown || backupState == ExportPathState.File || !destinationKnown ||
                destination.Intent == ExportWriteIntent.ConfirmedOverwrite && destinationState != ExportPathState.File)
            {
                return new(StatisticsExportPhase.Failed, RecoveryPath: backup is not null && (!backupKnown || backupState == ExportPathState.File) ? backup : directory ?? temporary,
                    Error: "导出提交失败，文件状态无法完整确认；已保留本次恢复文件。" + error.Message);
            }
            // The documented ordinary failure states retain D/T, with no B. New-file races leave the other writer's D intact.
            string? residue = CleanupOwnedTemporary(temporary, files);
            string? directoryCleanup = CleanupDirectory(directory, files);
            return new(StatisticsExportPhase.Failed, RecoveryPath: residue is not null ? temporary : directoryCleanup is not null ? directory : null,
                Error: "导出提交失败，目标文件未被覆盖。" + error.Message, Warning: Join(residue, directoryCleanup));
        }
        try
        {
            files.DeleteFile(backup!);
            files.DeleteDirectory(directory!);
            return new(StatisticsExportPhase.Completed, destination.Path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            bool backupKnown = TryProbe(files, backup, out var state);
            return new(StatisticsExportPhase.Completed, destination.Path, !backupKnown || state == ExportPathState.File ? backup : directory,
                Warning: "导出已完成，但原文件恢复备份未能完全清理：" + error.Message);
        }
    }

    private static bool TryProbe(IExportFileSystem files, string? path, out ExportPathState state)
    {
        state = ExportPathState.Missing;
        if (path is null) return true;
        try { state = files.Probe(path); return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }
    internal static string? CleanupOwnedTemporary(string? path, IExportFileSystem files)
    {
        if (path is null) return null;
        try { files.DeleteFile(path); return null; }
        catch (Exception error) when (error is not OutOfMemoryException) { return "临时导出文件清理失败：" + error.Message; }
    }
    private static string? CleanupDirectory(string? path, IExportFileSystem files)
    {
        if (path is null) return null;
        try { files.DeleteDirectory(path); return null; }
        catch (Exception error) when (error is not OutOfMemoryException) { return "恢复目录清理失败：" + error.Message; }
    }
    internal static string? Join(string? first, string? second) => first is null ? second : second is null ? first : first + "；" + second;
    private static int Win32Code(Exception error) => error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode :
        ((uint)error.HResult & 0xFFFF0000u) == 0x80070000u ? error.HResult & 0xFFFF : 0;
}
