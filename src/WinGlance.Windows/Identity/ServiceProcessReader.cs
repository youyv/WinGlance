using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinGlance.Windows.Identity;

[SupportedOSPlatform("windows")]
internal static class ServiceProcessReader
{
    public static HashSet<int> Read()
    {
        var result = new HashSet<int>();
        var manager = OpenSCManager(null, null, 4);
        if (manager == IntPtr.Zero) return result;
        var buffer = Marshal.AllocHGlobal(256 * 1024);
        try
        {
            uint resume = 0;
            do
            {
                var success = EnumServicesStatusEx(manager, 0, 0x30, 1, buffer, 256 * 1024,
                    out _, out var count, ref resume, null);
                var error = success ? 0 : Marshal.GetLastWin32Error();
                if (!success && error != 234) break;
                var stride = Marshal.SizeOf<ServiceEntry>();
                for (var index = 0; index < count; index++)
                {
                    var entry = Marshal.PtrToStructure<ServiceEntry>(buffer + (int)index * stride);
                    if (entry.Status.ProcessId is > 0 and <= int.MaxValue) result.Add((int)entry.Status.ProcessId);
                }
                if (success || count == 0) break;
            } while (resume != 0);
        }
        finally { Marshal.FreeHGlobal(buffer); CloseServiceHandle(manager); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceEntry { public IntPtr Name, DisplayName; public ServiceStatus Status; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public uint Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(IntPtr manager, uint infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufferSize, out uint bytesNeeded, out uint count, ref uint resume, string? group);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
