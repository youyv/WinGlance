using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinGlance.Statistics;

public sealed class StatisticsExportDestination
{
    private StatisticsExportDestination(string path, bool existed) { Path = path; ExistedAtSelection = existed; }
    public string Path { get; }
    public bool ExistedAtSelection { get; }
    public static StatisticsExportDestination Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = System.IO.Path.GetFullPath(path);
        return new(path, ExportFileSystem.Instance.Probe(path) == ExportPathState.File);
    }
}

internal enum ExportPathState { Missing, File }

// Kept narrow so tests can reproduce documented ReplaceFile partial-failure states.
internal interface IExportFileSystem
{
    ExportPathState Probe(string path);
    string CreateRecoveryDirectory(string parent);
    void Move(string source, string destination);
    void Replace(string source, string destination, string backup);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

internal sealed class ExportFileSystem : IExportFileSystem
{
    internal static readonly ExportFileSystem Instance = new();
    public ExportPathState Probe(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("导出目标必须是文件。");
            return ExportPathState.File;
        }
        catch (FileNotFoundException) { return ExportPathState.Missing; }
        // Directory-not-found, access-denied and other I/O states are unknown, never new-file intent.
    }
    public string CreateRecoveryDirectory(string parent)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string candidate = Path.Combine(parent, ".winglance-recovery-" + Guid.NewGuid().ToString("N"));
            if (CreateDirectory(candidate, 0)) return candidate;
            int error = Marshal.GetLastWin32Error();
            if (error != 183) throw new Win32Exception(error);
        }
        throw new IOException("无法创建独占的导出恢复目录。");
    }
    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);
    public void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup, ignoreMetadataErrors: false);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, nint securityAttributes);
}
