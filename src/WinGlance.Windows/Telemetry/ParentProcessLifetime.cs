using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WinGlance.Windows.Telemetry;

/// <summary>Tracks one verified kernel process object; PID reuse never changes the tracked parent.</summary>
[SupportedOSPlatform("windows")]
public sealed class ParentProcessLifetime : IDisposable
{
    private const long FileTimeEpochTicks = 504911232000000000;
    private readonly SafeProcessHandle _handle;
    private ParentProcessLifetime(SafeProcessHandle handle) => _handle = handle;

    public static ParentProcessLifetime? TryOpen(int pid, long expectedStartTimeUtcTicks)
    {
        if (pid <= 0 || expectedStartTimeUtcTicks <= 0) return null;
        var handle = OpenProcess(0x00100000 | 0x1000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid || !GetProcessTimes(handle, out long created, out _, out _, out _) ||
            created <= 0 || created > DateTime.MaxValue.Ticks - FileTimeEpochTicks ||
            created + FileTimeEpochTicks != expectedStartTimeUtcTicks ||
            WaitForSingleObject(handle, 0) != 258)
        {
            handle.Dispose();
            return null;
        }
        return new(handle);
    }

    public bool IsAlive => !_handle.IsClosed && !_handle.IsInvalid && WaitForSingleObject(_handle, 0) == 258;
    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
