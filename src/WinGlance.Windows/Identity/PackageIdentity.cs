using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinGlance.Windows.Identity;

internal sealed record PackageIdentity(string Family, string ApplicationId, string FullName);

[SupportedOSPlatform("windows")]
internal static class PackageIdentityReader
{
    public static PackageIdentity? Read(int pid, long expectedStartTimeUtcTicks)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) return null;
        if (expectedStartTimeUtcTicks != 0)
        {
            if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return null;
            try { if (DateTime.FromFileTimeUtc(creation).Ticks != expectedStartTimeUtcTicks) return null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        var family = ReadString(GetPackageFamilyName, process);
        if (family is null) return null;
        var application = ReadString(GetApplicationUserModelId, process);
        var fullName = ReadString(GetPackageFullName, process);
        // Family alone is not enough: a package may expose several independent applications.
        if (application is null || fullName is null || !application.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase)) return null;
        return new PackageIdentity(family, application, fullName);
    }

    public static string? DisplayName(string applicationId)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName("shell:AppsFolder\\" + applicationId, IntPtr.Zero, ref iid, out var item) < 0) return null;
        try
        {
            if (item.GetDisplayName(0, out var pointer) < 0 || pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(pointer); }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        finally { Marshal.FinalReleaseComObject(item); }
    }

    private delegate int StringQuery(SafeProcessHandle process, ref uint length, StringBuilder? value);
    private static string? ReadString(StringQuery query, SafeProcessHandle process)
    {
        uint length = 0;
        if (query(process, ref length, null) != 122 || length is 0 or > 4096) return null;
        var value = new StringBuilder((int)length);
        return query(process, ref length, value) == 0 ? value.ToString() : null;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint nameType, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IntPtr other, uint hint, out int order);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder? value);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
}
