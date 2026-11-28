using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WinGlance.Windows.Identity;

[SupportedOSPlatform("windows")]
internal static class ShellIconCache
{
    private const int MaximumFiles = 512;
    private static readonly string CacheDirectory = Path.Combine(WinGlance.Core.LocalDataPaths.DataDirectory, "Icons");

    public static string? Extract(string filePath, long modifiedTicks, long fileLength, long createdTicks = 0)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{filePath.ToUpperInvariant()}|{modifiedTicks}|{fileLength}|{createdTicks}|64")));
            var destination = Path.Combine(CacheDirectory, key + ".png");
            if (File.Exists(destination)) return destination;
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iid, out var factory) < 0) return null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                // ICONONLY prevents expensive document thumbnail handlers from running.
                if (factory.GetImage(new Size(64, 64), 0x4 | 0x1, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
                if (GetObject(bitmap, Marshal.SizeOf<Bitmap>(), out var details) == 0) return null;
                var width = details.Width;
                var height = Math.Abs(details.Height);
                if (width is <= 0 or > 512 || height is <= 0 or > 512) return null;
                var pixels = new byte[width * height * 4];
                var info = new BitmapInfo { Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                    Planes = 1, BitCount = 32, Compression = 0, SizeImage = (uint)pixels.Length
                }};
                var dc = GetDC(IntPtr.Zero);
                try
                {
                    if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;
                }
                finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
                var temporary = destination + ".tmp";
                WritePng(temporary, width, height, pixels);
                File.Move(temporary, destination, true);
                foreach (var old in new DirectoryInfo(CacheDirectory).EnumerateFiles("*.png")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaximumFiles))
                {
                    try { old.Delete(); } catch (IOException) { }
                }
                return destination;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                Marshal.FinalReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        { return null; }
    }

    private static void WritePng(string path, int width, int height, byte[] bgra)
    {
        using var output = File.Create(path);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);
        var opaque = true;
        for (var i = 3; i < bgra.Length; i += 4) if (bgra[i] != 0) { opaque = false; break; }
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 4 + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = (y * width + x) * 4;
                    var target = x * 4 + 1;
                    var alpha = opaque ? (byte)255 : bgra[source + 3];
                    // Shell bitmaps use premultiplied BGRA; PNG stores straight RGBA.
                    row[target] = Unpremultiply(bgra[source + 2], alpha);
                    row[target + 1] = Unpremultiply(bgra[source + 1], alpha);
                    row[target + 2] = Unpremultiply(bgra[source], alpha);
                    row[target + 3] = alpha;
                }
                zip.Write(row);
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static byte Unpremultiply(byte value, byte alpha) => alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);

    private static void WriteChunk(Stream stream, string type, byte[] payload)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, payload.Length);
        stream.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(payload);
        uint crc = 0xffffffff;
        foreach (var value in typeBytes.Concat(payload))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, uint flags, out IntPtr bitmap);
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Size(int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] private struct Bitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItemImageFactory factory);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr obj, int size, out Bitmap bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
