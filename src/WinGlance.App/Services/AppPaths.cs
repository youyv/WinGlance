namespace WinGlance.App.Services;

internal static class AppPaths
{
    // Opt-in developer diagnostics use separate settings, persistence and instance ownership.
    // Normal launches never set this variable.
    private static readonly string? DiagnosticRoot = Environment.GetEnvironmentVariable("WINGLANCE_DIAGNOSTICS_DIR");
    internal static bool IsDiagnostic => !string.IsNullOrWhiteSpace(DiagnosticRoot) && Path.IsPathFullyQualified(DiagnosticRoot);
    internal static string DataDirectory { get; } = IsDiagnostic ? Path.GetFullPath(DiagnosticRoot!) :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance");
    internal static bool DisableCharts { get; } = IsDiagnostic && Environment.GetEnvironmentVariable("WINGLANCE_DISABLE_CHARTS") == "1";
    internal static bool DisableMotion { get; } = IsDiagnostic && Environment.GetEnvironmentVariable("WINGLANCE_DISABLE_MOTION") == "1";
    internal static bool InjectFault(string name) => IsDiagnostic && File.Exists(Path.Combine(DataDirectory, "fault-" + name));
}
