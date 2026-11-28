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
    internal const string SpaceLimitMessage = "当前屏幕可用空间不足，部分悬浮条内容未显示；可切换为“仅网速”预设。";
    internal double TextScale { get; init; } = 1;
    internal double FontSizeDip { get; init; } = 12;
    internal double LineHeightDip { get; init; } = LineHeight;
    internal bool IsSpaceLimited { get; init; }
    internal static OverlayPreset Normalize(OverlayPreset value) => Enum.IsDefined(value) ? value : OverlayPreset.Standard;
    internal static double NormalizeTextScale(double value) => double.IsFinite(value) ? Math.Clamp(value, 1, 2.25) : 1;

    internal static OverlayPresentation Create(OverlayPreset preset, SystemSample? sample,
        bool isLive, string compactStatus, double availableWidthDip = 510,
        double textScale = 1, double availableHeightDip = double.PositiveInfinity, double dpiScale = 1)
    {
        preset = Normalize(preset);
        textScale = NormalizeTextScale(textScale);
        double fontSize = Math.Round(12 * textScale, 2);
        double lineHeight = Math.Ceiling(LineHeight * textScale);
        double preferred = Math.Ceiling((preset == OverlayPreset.Network ? 340 : 510) * textScale);
        dpiScale = double.IsFinite(dpiScale) && dpiScale > 0 ? Math.Clamp(dpiScale, .5, 8) : 1;
        // A minimum width must never override the actual monitor's work area.
        double width = Math.Min(preferred, double.IsFinite(availableWidthDip) ? Math.Max(0, availableWidthDip) : preferred);
        // GDI rounds its HFONT up to whole physical pixels. Wrap both renderers for
        // that actual font size and the separately rounded pixel padding, otherwise
        // a mixed Chinese/ASCII line can overflow at fractional DPI (27 * 1.5 = 40.5).
        double measureScale = Math.Max(textScale, Math.Ceiling(fontSize * dpiScale) / (12 * dpiScale));
        double contentWidth = Math.Max(0, Math.Min(width - HorizontalChrome,
            (OverlayPlacement.SizeForScale(dpiScale, width, 0).Width - Math.Ceiling(16 * dpiScale) - Math.Ceiling(60 * dpiScale)) / dpiScale));
        double availableHeight = double.IsFinite(availableHeightDip) ? Math.Max(0, availableHeightDip) : double.PositiveInfinity;
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
        bool limited = false;
        foreach (var group in groups)
        {
            string line = "";
            foreach (var segment in group)
            {
                string combined = line.Length == 0 ? segment : line + "   " + segment;
                if (Measure(combined, measureScale) <= contentWidth) { line = combined; continue; }
                if (line.Length != 0) { lines.Add(line); line = ""; }
                foreach (char character in segment)
                {
                    if (Measure(character.ToString(), measureScale) > contentWidth)
                    { limited = true; continue; }
                    if (line.Length > 0 && Measure(line + character, measureScale) > contentWidth)
                    { lines.Add(line.TrimEnd()); line = ""; }
                    line += character;
                }
            }
            if (line.Length != 0) lines.Add(line);
        }
        if (lines.Count == 0 && !limited) lines.Add("");
        int visibleLines = double.IsPositiveInfinity(availableHeight) ? lines.Count :
            (int)Math.Min(lines.Count, Math.Max(0, Math.Floor((availableHeight - 24) / lineHeight)));
        if (visibleLines < lines.Count) { limited = true; lines.RemoveRange(visibleLines, lines.Count - visibleLines); }
        if (limited)
        {
            // Extremely small work areas cannot display every glyph. Mark omitted text
            // explicitly where there is room, instead of drawing beyond the work area.
            if (lines.Count > 0)
            {
                string last = lines[^1];
                while (last.Length > 0 && Measure(last + "…", measureScale) > contentWidth) last = last[..^1];
                lines[^1] = Measure("…", measureScale) <= contentWidth ? last + "…" : "";
            }
            else if (availableHeight >= lineHeight + 24 && Measure("…", measureScale) <= contentWidth) lines.Add("…");
        }
        return new(string.Join('\n', lines), width, Math.Min(availableHeight, Math.Max(Math.Ceiling(58 * textScale), lines.Count * lineHeight + 24)))
        {
            TextScale = textScale, FontSizeDip = fontSize, LineHeightDip = lineHeight, IsSpaceLimited = limited
        };
    }

    // The measurement factor can be slightly above 225% because a GDI font rounds
    // upward to physical pixels; only the user-facing text factor is capped.
    internal static double Measure(string text, double textScale = 1) => text.Sum(c => c <= 127 ? 7.5 : 12.5d) * textScale;
}
