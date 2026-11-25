using System.Text.Json;

namespace WinGlance.App.Services;

public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance", "settings.json");
    public string Theme { get; set; } = "System";
    public bool HideSystem { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool ShowOverlay { get; set; }
    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    public void Save() => TrySave(out _);
    public bool TrySave(out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }
}
