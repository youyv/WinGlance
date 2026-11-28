using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinGlance.App.Services;

/// <summary>
/// A separate layered HWND keeps click-through independent of WinUI composition.
/// WS_EX_LAYERED + WS_EX_TRANSPARENT passes mouse input to other processes;
/// WS_EX_TRANSPARENT alone and HTTRANSPARENT do not provide that guarantee.
/// Paints only when the existing metric update or an appearance/display event changes it.
/// </summary>
internal sealed class ClickThroughOverlay : IDisposable
{
    private const string ClassName = "WinGlance.ClickThroughOverlay";
    private static readonly WindowProc Callback = WndProc;
    private static readonly Dictionary<nint, ClickThroughOverlay> Windows = [];
    private static bool _registered;
    private readonly Action _displayChanged;
    private readonly Action _closeRequested;
    private nint _dc, _bitmap, _oldBitmap, _font, _oldFont, _bits;
    private byte[] _pixels = [];
    private int _width, _height;
    private double _scale;
    private (string Text, bool Dark, bool HighContrast, double Opacity)? _lastPaint;
    private bool _shown;
    internal nint Handle { get; private set; }
    internal void Invalidate() => _lastPaint = null;
    internal void Hide()
    {
        if (Handle == 0) return;
        ShowWindow(Handle, 0);
        _shown = false;
    }

    internal ClickThroughOverlay(int x, int y, Action displayChanged, Action closeRequested)
    {
        _displayChanged = displayChanged;
        _closeRequested = closeRequested;
        if (!_registered)
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Callback,
                Instance = GetModuleHandle(null), ClassName = ClassName
            };
            if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        Handle = CreateWindowEx(0x00080000 | 0x00000020 | 0x08000000 | 0x00000080,
            ClassName, "WinGlance 悬浮条（鼠标穿透）", 0x80000000,
            x, y, 1, 1, 0, 0, GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Windows.Add(Handle, this);
    }

    internal (int X, int Y) Position
    {
        get
        {
            if (Handle == 0 || !GetWindowRect(Handle, out var rectangle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return (rectangle.Left, rectangle.Top);
        }
    }

    internal void Render(OverlayPresentation presentation, bool dark, bool highContrast, double opacity, int x, int y, double scale)
    {
        if (Handle == 0) throw new ObjectDisposedException(nameof(ClickThroughOverlay));
        string text = presentation.Text;
        var size = OverlayPlacement.SizeForScale(scale, presentation.WidthDip, presentation.HeightDip);
        if (_width != size.Width || _height != size.Height || _scale != scale)
        {
            CreateSurface(size.Width, size.Height, scale);
            _lastPaint = null;
        }
        opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, .2, 1) : .8;
        var paint = (text, dark, highContrast, opacity);
        if (_lastPaint == paint && _shown && Position == (x, y)) return;
        if (_lastPaint != paint)
        {
            Array.Clear(_pixels);
            Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
            var lines = text.Split('\n');
            int lineHeight = (int)Math.Ceiling(OverlayPresentation.LineHeight * scale);
            int top = Math.Max(0, (_height - lines.Length * lineHeight) / 2);
            foreach (var line in lines)
            {
                var rectangle = new Rect { Left = (int)Math.Ceiling(16 * scale), Top = top,
                    Right = _width - (int)Math.Ceiling((OverlayPresentation.HorizontalChrome - 16) * scale), Bottom = top + lineHeight };
                // Explicit shared line breaks; no truncation or renderer-dependent wrapping.
                DrawText(_dc, line, line.Length, ref rectangle, 0x20 | 0x4 | 0x800);
                top += lineHeight;
            }
            GdiFlush(); // DIB pixels must not be read while GDI is still drawing into them.
            Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
            uint background = highContrast ? FromColorRef(GetSysColor(5)) : dark ? 0x202020u : 0xF3F3F3u;
            uint foreground = highContrast ? FromColorRef(GetSysColor(8)) : dark ? 0xFFFFFFu : 0x1A1A1Au;
            CompositeTextMask(_pixels, _width, _height, (int)Math.Ceiling(8 * scale), background, foreground,
                highContrast ? (byte)255 : (byte)Math.Round(opacity * 255));
            Marshal.Copy(_pixels, 0, _bits, _pixels.Length);
            _lastPaint = paint;
        }
        var position = new Point { X = x, Y = y };
        var dimensions = new Size { Width = _width, Height = _height };
        var source = new Point();
        var blend = new Blend { ConstantAlpha = 255, AlphaFormat = 1 };
        if (!UpdateLayeredWindow(Handle, 0, ref position, ref dimensions, _dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!_shown)
        {
            if (!SetWindowPos(Handle, -1, x, y, _width, _height, 0x0010 | 0x0040))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _shown = true;
        }
    }

    private void CreateSurface(int width, int height, double scale)
    {
        ReleaseSurface();
        try
        {
            _dc = CreateCompatibleDC(0);
            if (_dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo
            {
                Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32
            };
            _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, 0, 0);
            if (_bitmap == 0 || _bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _oldBitmap = SelectObject(_dc, _bitmap);
            _font = CreateFont(-(int)Math.Ceiling(12 * scale), 0, 0, 0, 400,
                0, 0, 0, 1, 0, 0, 4, 0, "Consolas");
            if (_font == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _oldFont = SelectObject(_dc, _font);
            SetTextColor(_dc, 0xFFFFFF);
            SetBkMode(_dc, 1);
            _width = width; _height = height; _scale = scale;
            _pixels = new byte[checked(width * height * 4)];
        }
        catch { ReleaseSurface(); throw; }
    }

    // White text on a black mask is converted to premultiplied BGRA. Background opacity
    // never dims the text; use grayscale antialiasing instead of a ClearType color fringe.
    internal static void CompositeTextMask(Span<byte> pixels, int width, int height, int radius,
        uint background, uint foreground, byte opacity)
    {
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 4;
            int coverage = Math.Max(pixels[index], Math.Max(pixels[index + 1], pixels[index + 2]));
            double dx = Math.Max(radius - x - .5, x + .5 - (width - radius));
            double dy = Math.Max(radius - y - .5, y + .5 - (height - radius));
            int backgroundAlpha = dx > 0 && dy > 0 && dx * dx + dy * dy > radius * radius ? 0 : opacity;
            int remainder = (backgroundAlpha * (255 - coverage) + 127) / 255;
            for (int channel = 0; channel < 3; channel++)
                pixels[index + channel] = (byte)((((foreground >> (channel * 8)) & 255) * coverage +
                    ((background >> (channel * 8)) & 255) * remainder + 127) / 255);
            pixels[index + 3] = (byte)(coverage + remainder);
        }
    }

    private static uint FromColorRef(uint color) => ((color & 255) << 16) | (color & 0xFF00) | ((color >> 16) & 255);
    private static nint WndProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0010 && Windows.TryGetValue(window, out var closingOverlay))
        {
            closingOverlay._closeRequested();
            return 0;
        }
        if ((message is 0x02E0 or 0x007E or 0x001A) && Windows.TryGetValue(window, out var overlay))
            overlay._displayChanged();
        if (message == 0x0021) return 3; // MA_NOACTIVATE, including accessibility-triggered hit tests.
        return DefWindowProc(window, message, wParam, lParam);
    }
    private void ReleaseSurface()
    {
        if (_dc != 0 && _oldFont != 0) SelectObject(_dc, _oldFont);
        if (_font != 0) DeleteObject(_font);
        if (_dc != 0 && _oldBitmap != 0) SelectObject(_dc, _oldBitmap);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_dc != 0) DeleteDC(_dc);
        _dc = _font = _oldFont = _bitmap = _oldBitmap = _bits = 0;
        _pixels = [];
    }
    public void Dispose()
    {
        nint window = Handle;
        Handle = 0;
        if (window != 0) { Windows.Remove(window); DestroyWindow(window); }
        ReleaseSurface();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WindowProc Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
    }
    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint screenDC, ref Point position, ref Size size, nint sourceDC, ref Point source, uint colorKey, ref Blend blend, uint flags);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint source);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int length, ref Rect rectangle, uint flags);
}
