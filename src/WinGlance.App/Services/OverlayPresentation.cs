using WinGlance.App.ViewModels;
using WinGlance.Core;

namespace WinGlance.App.Services;

public enum OverlayPreset { Standard = 0, Network = 1, Memory = 2, All = 3 }

// Both renderers consume the same explicit lines and DIP dimensions. Conservative
// Consolas/fallback advances leave room for different GDI and XAML text metrics.
internal sealed record OverlayPresentation(string Text, double WidthDip, double HeightDip)
{
    internal const double LineHeight = 20;
    internal const double HorizontalChrome = 76;
    internal static OverlayPreset Normalize(OverlayPreset value) => Enum.IsDefined(value) ? value : OverlayPreset.Standard;

    internal static OverlayPresentation Create(OverlayPreset preset, SystemSample? sample,
        bool isLive, string compactStatus, double availableWidthDip = 510)
    {
        preset = Normalize(preset);
        double preferred = preset == OverlayPreset.Network ? 340 : 510;
        double width = Math.Min(preferred, double.IsFinite(availableWidthDip) ? Math.Max(96, availableWidthDip) : preferred);
        double contentWidth = width - HorizontalChrome;
        var groups = new List<string[]>();
        if (!isLive || sample is null)
            groups.Add([compactStatus]);
        else
        {
            var main = new List<string> { $"↓ {Display.Rate(sample.DownloadBytesPerSecond)}", $"↑ {Display.Rate(sample.UploadBytesPerSecond)}" };
            if (preset != OverlayPreset.Network)
                main.AddRange([$"CPU {Display.Percent(sample.CpuPercent)}", $"GPU {Display.Percent(sample.GpuPercent)}"]);
            groups.Add(main.ToArray());
            if (preset is OverlayPreset.Memory or OverlayPreset.All)
            {
                bool known = sample.TotalMemoryBytes > 0 && sample.AvailableMemoryBytes >= 0 && sample.AvailableMemoryBytes <= sample.TotalMemoryBytes;
                double? used = known ? sample.TotalMemoryBytes - sample.AvailableMemoryBytes : null;
                double? total = sample.TotalMemoryBytes > 0 ? sample.TotalMemoryBytes : null;
                groups.Add([$"内存 {Display.Bytes(used)} / {Display.Bytes(total)}", Display.Percent(used / total * 100)]);
            }
            if (preset == OverlayPreset.All)
                groups.Add([$"读 {Display.Rate(sample.DiskReadBytesPerSecond)}", $"写 {Display.Rate(sample.DiskWriteBytesPerSecond)}"]);
        }
        var lines = new List<string>();
        foreach (var group in groups)
        {
            string line = "";
            foreach (var segment in group)
            {
                string combined = line.Length == 0 ? segment : line + "   " + segment;
                if (Measure(combined) <= contentWidth) { line = combined; continue; }
                if (line.Length != 0) { lines.Add(line); line = ""; }
                foreach (char character in segment)
                {
                    if (line.Length > 0 && Measure(line + character) > contentWidth)
                    { lines.Add(line.TrimEnd()); line = ""; }
                    line += character;
                }
            }
            if (line.Length != 0) lines.Add(line);
        }
        if (lines.Count == 0) lines.Add("等待数据");
        return new(string.Join('\n', lines), width, Math.Max(58, lines.Count * LineHeight + 24));
    }

    internal static double Measure(string text) => text.Sum(c => c <= 127 ? 7.5 : 12d);
}
