using System.Text.Json;

namespace WinGlance.App.Services;

public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance", "settings.json");
    private static readonly object SaveLock = new();
    public string Theme { get; set; } = "System";
    public bool HideSystem { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool ShowOverlay { get; set; }
    public bool OverlayClickThrough { get; set; }
    public double OverlayOpacity { get; set; } = .8;
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public string? NetworkInterfaceId { get; set; }
    public string? PhysicalDiskId { get; set; }
    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    public void Save() => TrySave(out _);
    public Task<string?> SaveAsync()
    {
        string json = JsonSerializer.Serialize(this);
        return Task.Run(() => { Write(json, out var error); return error; });
    }
    public bool TrySave(out string? error)
        => Write(JsonSerializer.Serialize(this), out error);
    private static bool Write(string json, out string? error)
    {
        try
        {
            lock (SaveLock)
            {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Same-directory replacement avoids a truncated settings file on interrupted writes.
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, true);
            }
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
