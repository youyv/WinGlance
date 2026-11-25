using WinGlance.App.Services;

internal static class OverlayAppearanceRegression
{
    internal static void Run(Action<bool, string> check)
    {
        // A 3-pixel text mask: clear background, half-covered glyph edge, opaque glyph.
        byte[] pixels = [0, 0, 0, 0, 128, 128, 128, 0, 255, 255, 255, 0];
        ClickThroughOverlay.CompositeTextMask(pixels, 3, 1, 0, 0x202020, 0xFFFFFF, 51);
        check(pixels[3] == 51 && pixels[0] == 6 && pixels[1] == 6 && pixels[2] == 6,
            "native overlay uses premultiplied background pixels at selected opacity");
        check(pixels[11] == 255 && pixels[8] == 255 && pixels[9] == 255 && pixels[10] == 255,
            "native overlay background transparency leaves fully covered text opaque");
        check(pixels[7] == 153 && pixels[4] <= pixels[7] && pixels[5] <= pixels[7] && pixels[6] <= pixels[7],
            "native overlay antialiased glyph edges remain valid premultiplied pixels");
        byte[] corners = new byte[8 * 8 * 4];
        ClickThroughOverlay.CompositeTextMask(corners, 8, 8, 4, 0xF3F3F3, 0x1A1A1A, 204);
        check(corners[3] == 0 && corners[(4 * 8 + 4) * 4 + 3] == 204,
            "native overlay rounded corners are transparent while central background keeps opacity");
    }
}
