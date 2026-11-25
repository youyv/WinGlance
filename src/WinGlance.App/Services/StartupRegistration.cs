using Microsoft.Win32;

namespace WinGlance.App.Services;

public enum StartupRegistrationState
{
    Disabled,
    Enabled,
    DifferentCommand,
    InvalidValue
}

public sealed record StartupRegistrationStatus(StartupRegistrationState State, string? RegisteredCommand, bool StartMinimized = false)
{
    public bool IsEnabled => State == StartupRegistrationState.Enabled;
    public bool HasRegistration => State != StartupRegistrationState.Disabled;
}

/// <summary>Manages this application's current-user Run entry. Windows may separately disable startup apps.</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinGlance";

    public static StartupRegistrationStatus GetStatus()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key is null || !Array.Exists(key.GetValueNames(), name =>
                string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)))
            return new(StartupRegistrationState.Disabled, null);

        var value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key.GetValueKind(ValueName) != RegistryValueKind.String || value is not string command ||
            string.IsNullOrWhiteSpace(command))
            return new(StartupRegistrationState.InvalidValue, value as string);

        string currentCommand;
        try { currentCommand = GetCurrentCommand(); }
        catch (InvalidOperationException)
        {
            // An unusable current path must not prevent removing an existing registration.
            return new(StartupRegistrationState.DifferentCommand, command);
        }

        bool minimized = string.Equals(command, currentCommand + " --minimized", StringComparison.OrdinalIgnoreCase);
        var state = string.Equals(command, currentCommand, StringComparison.OrdinalIgnoreCase) || minimized
            ? StartupRegistrationState.Enabled
            : StartupRegistrationState.DifferentCommand;
        return new(state, command, minimized);
    }

    /// <summary>Changes only the WinGlance value and returns its verified state. Errors are reported to the caller.</summary>
    public static StartupRegistrationStatus SetEnabled(bool enabled, bool startMinimized = false)
    {
        if (enabled)
        {
            var command = GetCurrentCommand(startMinimized);
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException("无法打开当前用户的开机启动设置。");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            // Disabling must also work when the application was moved or the old entry is malformed.
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        var status = GetStatus();
        var expected = enabled ? StartupRegistrationState.Enabled : StartupRegistrationState.Disabled;
        if (status.State != expected || (enabled && status.StartMinimized != startMinimized))
            throw new IOException("开机启动设置未能保存，或已被其他程序更改，请重试。");
        return status;
    }

    private static string GetCurrentCommand(bool minimized = false)
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), "WinGlance.App.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new InvalidOperationException("无法确认应用程序路径，请通过 WinGlance.App.exe 运行应用后设置开机启动。");

        // Run uses a command line, so quote even paths that do not currently contain spaces.
        var command = BuildCommand(path, minimized);
        if (command.Length > 260)
            throw new InvalidOperationException("应用程序路径过长，无法设置开机启动，请将 WinGlance 移至较短的路径。");
        return command;
    }

    internal static string BuildCommand(string path, bool minimized) => $"\"{path}\"" + (minimized ? " --minimized" : "");
    internal static bool RequestsMinimized(IEnumerable<string> arguments) => arguments.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
    internal static bool ShouldHideOnLaunch(IEnumerable<string> arguments, bool trayAvailable) => trayAvailable && RequestsMinimized(arguments);
}
