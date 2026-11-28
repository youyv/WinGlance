using WinGlance.App.Services;

internal static class OverlayPixelRoundingRegression
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (pixels, scale) in new[] { (900, 1.75), (712, 2.75) })
        {
            double available = pixels / scale;
            check(available * scale > pixels, $"fixture exercises positive floating-point noise at {pixels}px/{scale}");
            var layout = OverlayPresentation.Create(OverlayPreset.Standard, null, false,
                "采集已断开 · 最后观测 12:34:56", available, 2.25, available);
            var size = OverlayPlacement.SizeForScale(scale, layout.WidthDip, layout.HeightDip);
            check(size.Width == pixels && size.Height <= pixels,
                $"work-area clamping survives DPI division/multiplication at {pixels}px/{scale}");
            var both = OverlayPlacement.SizeForScale(scale, available, available);
            check(both == (pixels, pixels), $"both dimensions tolerate one-ULP integer roundtrip at {scale}");
        }
        foreach (double pixels in new[] { 900.000001, 900.25, 900.5, 900.75 })
            check(OverlayPlacement.SizeForScale(1, pixels, pixels) == (901, 901),
                $"genuine fractional dimensions still round upward: {pixels:R}");
        check(OverlayPlacement.SizeForScale(1, 900, 899.999999) == (900, 900),
            "exact integer and values immediately below it preserve normal ceiling");
        check(OverlayPlacement.SizeForScale(1, Math.BitIncrement(Math.BitIncrement(900d)), 1).Width == 901,
            "rounding tolerance is limited to one representable value above the integer");
    }
}
