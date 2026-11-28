namespace WinGlance.Core;

/// <summary>Routes opt-in diagnostic files away from the normal user's application data.</summary>
public static class LocalDataPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory(
        Environment.GetEnvironmentVariable("WINGLANCE_DIAGNOSTICS_DIR"));

    public static string ResolveDataDirectory(string? diagnosticDirectory) =>
        !string.IsNullOrWhiteSpace(diagnosticDirectory) && Path.IsPathFullyQualified(diagnosticDirectory)
            ? Path.GetFullPath(diagnosticDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance");
}
