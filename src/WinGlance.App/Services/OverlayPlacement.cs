namespace WinGlance.App.Services;

internal readonly record struct WorkArea(int X, int Y, int Width, int Height);
internal static class OverlayPlacement
{
    // XAML dimensions are DIPs; AppWindow.Resize accepts physical pixels.
    internal static (int Width, int Height) SizeForScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        scale = Math.Clamp(scale, .5, 8);
        return ((int)Math.Ceiling(510 * scale), (int)Math.Ceiling(58 * scale));
    }

    internal static (int X, int Y) Recover(int x, int y, int width, int height, IReadOnlyList<WorkArea> areas)
    {
        if (areas.Count == 0) return (x, y);
        // Choose the monitor with the closest usable position, including negative desktop coordinates.
        var best = areas.Select(a =>
        {
            int cx = Math.Clamp(x, a.X, a.X + Math.Max(0, a.Width - width));
            int cy = Math.Clamp(y, a.Y, a.Y + Math.Max(0, a.Height - height));
            double distance = Math.Pow((double)cx - x, 2) + Math.Pow((double)cy - y, 2);
            return (X: cx, Y: cy, Distance: distance);
        }).MinBy(p => p.Distance);
        return (best.X, best.Y);
    }
}
