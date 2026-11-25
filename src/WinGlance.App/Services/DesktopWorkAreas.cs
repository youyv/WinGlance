using System.Runtime.InteropServices;

namespace WinGlance.App.Services;

internal static class DesktopWorkAreas
{
    internal static IReadOnlyList<WorkArea> Get()
    {
        var areas = new List<WorkArea>();
        MonitorProc callback = (nint monitor, nint dc, ref Rect rectangle, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) areas.Add(new(info.Work.Left, info.Work.Top,
                info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        return areas;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool MonitorProc(nint monitor, nint dc, ref Rect rectangle, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
