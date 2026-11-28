using WinGlance.App.Services;
using WinGlance.App.ViewModels;
using WinGlance.Core;

internal static class OverlayPresentationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var sample = new SystemSample(12.3, 16L << 30, 6L << 30, 123456, 45678, 98765, 43210, 4.5, "fixture");
        var standard = OverlayPresentation.Create(OverlayPreset.Standard, sample, true, "实时");
        check(standard.WidthDip == 510 && standard.HeightDip == 58 && !standard.Text.Contains('\n'), "default overlay remains a single 510x58 DIP standard row");
        var network = OverlayPresentation.Create(OverlayPreset.Network, sample, true, "实时");
        var memory = OverlayPresentation.Create(OverlayPreset.Memory, sample, true, "实时");
        var all = OverlayPresentation.Create(OverlayPreset.All, sample, true, "实时");
        check(!network.Text.Contains("CPU") && network.Text.Contains("↓") && network.Text.Contains("↑"), "network preset contains both selected-interface rates");
        check(memory.Text.Contains("10 GB / 16 GB") && memory.Text.Contains("62.5%") && memory.Text.Split('\n').Length == 2, "memory preset uses a separate line with used,total,percentage");
        check(all.Text.Contains("读") && all.Text.Contains("写") && all.Text.Split('\n').Length == 3, "all preset adds selected-disk read/write line");
        check(OverlayPresentation.Normalize((OverlayPreset)999) == OverlayPreset.Standard && OverlayPresentation.Create((OverlayPreset)999, sample, true, "实时") == standard, "unknown persisted preset falls back to standard");
        double huge = 1023.9 * Math.Pow(1024, 4);
        var large = sample with { DownloadBytesPerSecond = huge, UploadBytesPerSecond = huge, DiskReadBytesPerSecond = huge, DiskWriteBytesPerSecond = huge, CpuPercent = 100, GpuPercent = 100 };
        foreach (double scale in new[] { 1d, 1.5, 2d })
        foreach (double areaWidth in new[] { 1920d, 640d })
        foreach (var preset in Enum.GetValues<OverlayPreset>())
        {
            var layout = OverlayPresentation.Create(preset, large, true, "实时", areaWidth / scale);
            var size = OverlayPlacement.SizeForScale(scale, layout.WidthDip, layout.HeightDip);
            check(size.Width <= areaWidth && layout.Text.Split('\n').All(line => OverlayPresentation.Measure(line) <= layout.WidthDip - OverlayPresentation.HorizontalChrome), $"{preset} preserves bounded full lines at scale {scale}, workarea {areaWidth}");
            check(layout.Text.Contains(Display.Rate(huge)), $"{preset} preserves TB/s boundary at scale {scale}, workarea {areaWidth}");
            var position = OverlayPlacement.Recover(9999, -9999, size.Width, size.Height, [new(-(int)areaWidth, 0, (int)areaWidth, 900)]);
            check(position.X >= -areaWidth && position.X + size.Width <= 0 && position.Y >= 0 && position.Y + size.Height <= 900, "preset recovers within negative-coordinate monitor bounds");
        }
        var unknown = OverlayPresentation.Create(OverlayPreset.All, sample with { CpuPercent = null, GpuPercent = double.NaN, TotalMemoryBytes = 0, AvailableMemoryBytes = 0, DownloadBytesPerSecond = null, DiskReadBytesPerSecond = null }, true, "实时");
        check(unknown.Text.Contains("CPU —") && unknown.Text.Contains("GPU —") && unknown.Text.Contains("内存 — / —") && !unknown.Text.Contains("NaN"), "unknown resource values stay unknown in every preset");
        const string stale = "采集已断开 · 上次观测 12:34:56";
        foreach (var preset in Enum.GetValues<OverlayPreset>())
        {
            var state = OverlayPresentation.Create(preset, sample, false, stale, 320);
            check(state.Text.Replace("\n", "").Replace(" ", "") == stale.Replace(" ", "") && !state.Text.Contains("CPU"), "all presets show full last-observation status without stale live numbers");
        }
        check(OverlayPresentation.Create(OverlayPreset.All, null, true, "等待新数据").Text == "等待新数据", "missing fresh frame never fabricates resource values");
    }
}
