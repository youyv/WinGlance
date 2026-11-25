using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinGlance.Core;
using Microsoft.Win32.SafeHandles;

namespace WinGlance.App.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private NotifyIconData _data;
    public Action? ShowRequested { get; set; }
    public Action? OverlayRequested { get; set; }
    public Action? ExitRequested { get; set; }
    public Action? SessionQuery { get; set; }
    public Action<bool>? SessionEnd { get; set; }
    public Action? DisplayChanged { get; set; }
    public bool IsAvailable { get; private set; }
    private readonly uint _taskbarCreated;
    private readonly nint _ownedIcon;
    private bool _disposed;
    public TrayIcon(nint window)
    {
        _window = window;
        _callback = WndProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _ownedIcon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "WinGlance.ico"), 1, 32, 32, 0x10);
        _data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Flags = 1 | 2 | 4, Message = 0x8001,
            Icon = _ownedIcon != 0 ? _ownedIcon : LoadIcon(0, (nint)32512), Tip = "WinGlance · 系统资源概览", Info = "", InfoTitle = "" };
        SetWindowSubclass(window, _callback, 42, 0);
        IsAvailable = Shell_NotifyIcon(0, ref _data);
    }
    public void Update(string text)
    {
        _data.Tip = text.Length > 127 ? text[..127] : text;
        if (IsAvailable) Shell_NotifyIcon(1, ref _data);
    }
    private nint WndProc(nint hwnd, uint msg, nuint w, nint l, nuint id, nuint data)
    {
        if (msg == 0x0011) { SessionQuery?.Invoke(); return 1; } // WM_QUERYENDSESSION: allow shutdown
        if (msg == 0x0016) { SessionEnd?.Invoke(w != 0); return 0; } // WM_ENDSESSION, including cancellation
        if (msg is 0x007E or 0x001A) DisplayChanged?.Invoke(); // display/work-area changes
        if (msg == _taskbarCreated) IsAvailable = Shell_NotifyIcon(0, ref _data);
        if (msg == 0x8001)
        {
            if ((uint)l == 0x0202 || (uint)l == 0x0203) ShowRequested?.Invoke();
            if ((uint)l == 0x0205)
            {
                var menu = CreatePopupMenu();
                AppendMenu(menu, 0, 1, "打开 WinGlance");
                AppendMenu(menu, 0, 2, "显示 / 隐藏悬浮条");
                AppendMenu(menu, 0x800, 0, null);
                AppendMenu(menu, 0, 3, "退出");
                GetCursorPos(out var point);
                SetForegroundWindow(hwnd);
                uint selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, hwnd, 0);
                DestroyMenu(menu);
                if (selected == 1) ShowRequested?.Invoke();
                if (selected == 2) OverlayRequested?.Invoke();
                if (selected == 3) ExitRequested?.Invoke();
            }
        }
        return DefSubclassProc(hwnd, msg, w, l);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Shell_NotifyIcon(2, ref _data); RemoveWindowSubclass(_window, _callback, 42); if (_ownedIcon != 0) DestroyIcon(_ownedIcon); }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Message; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint w, nint l, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}

public static class ProcessActions
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
        { "system", "registry", "idle", "smss", "csrss", "wininit", "winlogon", "lsass", "services", "secure system" };
    public static void OpenLocation(string? path)
    {
        if (path is null || !File.Exists(path)) throw new FileNotFoundException("文件不存在或路径不可访问。");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
    public static void Properties(string? path)
    {
        if (path is null || !File.Exists(path)) throw new FileNotFoundException("文件不存在或路径不可访问。");
        var info = new ShellExecuteInfo { Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x0000000C, Verb = "properties", File = path, Show = 1 };
        if (!ShellExecuteEx(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static void Terminate(ProcessSample sample)
    {
        if (sample.Software.Category == SoftwareCategory.WindowsSystem || sample.Identity.Pid <= 4 ||
            ProtectedNames.Contains(Path.GetFileNameWithoutExtension(sample.Software.ProcessName)))
            throw new InvalidOperationException("不允许在此结束 Windows 系统关键进程。");
        if (sample.Identity.Pid == Environment.ProcessId) throw new InvalidOperationException("请通过托盘菜单退出 WinGlance。");
        using var process = OpenProcess(0x1000 | 0x0001, false, sample.Identity.Pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetProcessTimes(process, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (sample.Identity.StartTimeUtcTicks <= 0 || DateTime.FromFileTimeUtc(created).Ticks != sample.Identity.StartTimeUtcTicks)
            throw new InvalidOperationException("进程已退出或 PID 已被复用，请刷新后重试。");
        if (!IsProcessCritical(process, out bool critical)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (critical) throw new InvalidOperationException("该进程被 Windows 标记为关键进程，不能结束。");
        if (!TerminateProcess(process, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show; public nint Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey; public uint HotKey; public nint Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
}
