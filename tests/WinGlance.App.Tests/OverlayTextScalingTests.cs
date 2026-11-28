using System.ComponentModel;
using System.Runtime.InteropServices;
using WinGlance.App.Services;
using WinGlance.Core;

internal static class OverlayTextScalingTests
{
    internal static void Run(Action<bool, string> check)
    {
        double huge = 1023.9 * Math.Pow(1024, 4);
        var sample = new SystemSample(100, 16L << 30, 6L << 30, huge, huge, huge, huge, 100, "text-scale fixture");
        const string stale = "采集已断开 · 上次观测 12:34:56";
        check(OverlayPresentation.NormalizeTextScale(double.NaN) == 1 &&
            OverlayPresentation.NormalizeTextScale(double.PositiveInfinity) == 1 &&
            OverlayPresentation.NormalizeTextScale(-1) == 1 && OverlayPresentation.NormalizeTextScale(9) == 2.25,
            "invalid accessibility factors normalize to the supported 100%-225% range");

        // This measurement intentionally does not call OverlayPresentation.Measure.
        // GDI selects the actual installed Consolas font and fallback glyph metrics.
        foreach (double textScale in new[] { 1d, 1.5, 2.25 })
        foreach (double dpiScale in new[] { 1d, 1.25, 1.5, 1.75, 2d, 2.75 })
        foreach (int areaWidth in new[] { 1920, 640, 900, 712 })
        foreach (var preset in Enum.GetValues<OverlayPreset>())
        foreach (bool live in new[] { true, false })
        {
            var layout = OverlayPresentation.Create(preset, sample, live, stale, areaWidth / dpiScale, textScale, 2160 / dpiScale, dpiScale);
            var dimensions = OverlayPlacement.SizeForScale(dpiScale, layout.WidthDip, layout.HeightDip);
            int fontPixels = (int)Math.Ceiling(layout.FontSizeDip * dpiScale);
            int linePixels = (int)Math.Ceiling(layout.LineHeightDip * dpiScale);
            int left = (int)Math.Ceiling(16 * dpiScale);
            int right = dimensions.Width - (int)Math.Ceiling(60 * dpiScale);
            using var font = new GdiTextMeasurement(fontPixels);
            var actualLines = layout.Text.Split('\n').Select(line =>
            {
                var actual = font.Measure(line);
                return (Text: line, Width: actual.Width, Height: actual.Height);
            }).ToArray();
            bool fits = actualLines.All(line => line.Width <= right - left && line.Height <= linePixels);
            string overflow = fits ? "" : "; " + string.Join("; ", actualLines.Where(line => line.Width > right - left || line.Height > linePixels)
                .Select(line => $"'{line.Text}' is {line.Width}x{line.Height}px, available {right - left}x{linePixels}px, font {fontPixels}px"));
            check(fits && dimensions.Width <= areaWidth && dimensions.Height <= 2160 && !layout.IsSpaceLimited,
                $"actual GDI glyphs fit {preset}/{(live ? "live" : "stale")}, text {textScale:P0}, DPI {dpiScale}, workarea {areaWidth}px{overflow}");
            if (!live)
                check(layout.Text.Replace("\n", "").Replace(" ", "") == stale.Replace(" ", ""),
                    "scaled stale state retains the complete observation message");
        }

        var defaultLayout = OverlayPresentation.Create(OverlayPreset.Standard, null, false, "等待数据", 1920);
        var largeLayout = OverlayPresentation.Create(OverlayPreset.Standard, null, false, "等待数据", 1920, 2.25);
        check(defaultLayout.WidthDip == 510 && defaultLayout.HeightDip == 58 && defaultLayout.FontSizeDip == 12 &&
            largeLayout.WidthDip == 1148 && largeLayout.FontSizeDip == 27 && largeLayout.LineHeightDip == 45,
            "default dimensions remain unchanged while accessibility changes explicit font and line metrics once");

        foreach (double availableWidth in new[] { 20d, 75d, 76d, 80d, 100d })
        {
            var constrained = OverlayPresentation.Create(OverlayPreset.All, sample, true, "实时", availableWidth, 2.25, 200);
            check(constrained.WidthDip <= availableWidth && constrained.HeightDip <= 200 && constrained.IsSpaceLimited,
                "an extremely narrow workarea stays bounded and explicitly marks omitted content");
        }
        var shortArea = OverlayPresentation.Create(OverlayPreset.All, sample, true, "实时", 640, 2.25, 50);
        check(shortArea.HeightDip <= 50 && shortArea.IsSpaceLimited && !string.IsNullOrWhiteSpace(OverlayPresentation.SpaceLimitMessage),
            "an extremely short workarea reports limited content instead of overflowing vertically");

        // Width and height can both be workarea-clamped while the text metrics change.
        var small = OverlayPresentation.Create(OverlayPreset.All, sample, true, "实时", 120, 1, 50);
        var large = OverlayPresentation.Create(OverlayPreset.All, sample, true, "实时", 120, 1.5, 50);
        check(small.WidthDip == large.WidthDip && small.HeightDip == large.HeightDip &&
            small.FontSizeDip != large.FontSizeDip && small.LineHeightDip != large.LineHeightDip,
            "same-size surfaces still receive distinct font and line metrics for native cache invalidation");
    }

    private sealed class GdiTextMeasurement : IDisposable
    {
        private readonly nint _dc, _font, _previous;
        internal GdiTextMeasurement(int fontPixels)
        {
            _dc = CreateCompatibleDC(0);
            if (_dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _font = CreateFont(-fontPixels, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Consolas");
            if (_font == 0) { DeleteDC(_dc); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            _previous = SelectObject(_dc, _font);
        }
        internal Size Measure(string text)
        {
            if (!GetTextExtentPoint32(_dc, text, text.Length, out var dimensions))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return dimensions;
        }
        public void Dispose() { SelectObject(_dc, _previous); DeleteObject(_font); DeleteDC(_dc); }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int Width, Height; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFont(int height, int width, int escape, int orientation, int weight,
        uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint family, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetTextExtentPoint32(nint dc, string text, int count, out Size result);
}
