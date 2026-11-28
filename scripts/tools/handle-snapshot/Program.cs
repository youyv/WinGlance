using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// Read-only diagnostic helper for one explicitly identified, isolated WinGlance process.
// No system-wide scan, handle names, target handle closure, privileges or forced collection.
if (args.Length != 4 || !int.TryParse(args[0], out int pid) || !long.TryParse(args[1], out long expectedTicks))
{
    Console.Error.WriteLine("Usage: handle-snapshot PID START_UTC_TICKS EXACT_EXE_PATH OUTPUT_JSON");
    return 2;
}
if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The handle snapshot layout is x64 only.");
string expectedPath = Path.GetFullPath(args[2]);
if (!string.Equals(Path.GetFileName(expectedPath), "WinGlance.App.exe", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Specify an isolated WinGlance.App.exe.");
long started = Stopwatch.GetTimestamp();
using SafeProcessHandle process = Native.OpenProcess(0x0400 | 0x0040 | 0x1000, false, pid);
if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
if (!Native.GetProcessTimes(process, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
long actualTicks = DateTime.FromFileTimeUtc(created).Ticks;
var pathBuffer = new StringBuilder(32768);
uint pathLength = (uint)pathBuffer.Capacity;
if (!Native.QueryFullProcessImageName(process, 0, pathBuffer, ref pathLength)) throw new Win32Exception(Marshal.GetLastWin32Error());
if (actualTicks != expectedTicks || !string.Equals(Path.GetFullPath(pathBuffer.ToString()), expectedPath, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The process identity does not match the requested isolated instance.");

if (!Native.GetProcessHandleCount(process, out uint beforeCount)) throw new Win32Exception(Marshal.GetLastWin32Error());
var entries = Snapshot(process);
var types = new List<object>();
foreach (var group in entries.GroupBy(entry => entry.ObjectTypeIndex).OrderByDescending(group => group.Count()))
{
    string? name = null;
    var errors = new List<string>();
    foreach (var entry in group.Take(3))
    {
        if (!Native.DuplicateHandle(process, entry.HandleValue, Native.GetCurrentProcess(), out nint duplicate, 0, false, 2))
        { errors.Add($"DuplicateHandle: {Marshal.GetLastWin32Error()}"); continue; }
        try { name = TypeName(duplicate); }
        catch (Exception error) { errors.Add(error.Message); }
        finally { Native.CloseHandle(duplicate); }
        if (name is not null) break;
    }
    types.Add(new { TypeIndex = group.Key, Name = name, Count = group.Count(), Errors = errors });
}
Native.GetProcessHandleCount(process, out uint afterCount);
var report = new
{
    CapturedUtc = DateTimeOffset.UtcNow,
    ProcessId = pid,
    StartUtcTicks = actualTicks,
    ExecutablePath = expectedPath,
    SnapshotHandles = entries.Count,
    ProcessCountBefore = beforeCount,
    ProcessCountAfter = afterCount,
    ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
    Types = types,
    Caveat = "ProcessHandleInformation is a native diagnostic interface. One instantaneous snapshot; handles can close/reuse while type representatives are duplicated. No object names or target handle closures. Type count does not identify the allocation stack."
};
string output = Path.GetFullPath(args[3]);
using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
    JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(JsonSerializer.Serialize(new { ProcessId = pid, Count = entries.Count, Types = types, report.ElapsedMilliseconds }));
return 0;

static List<HandleEntry> Snapshot(SafeProcessHandle process)
{
    int length = 64 * 1024;
    for (int attempt = 0; attempt < 8 && length <= 16 * 1024 * 1024; attempt++)
    {
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            int status = Native.NtQueryInformationProcess(process, 51, buffer, length, out int required);
            if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023))
            { length = Math.Max(checked(length * 2), checked(required + 4096)); continue; }
            if (status < 0) throw new InvalidOperationException($"ProcessHandleInformation NTSTATUS 0x{status:X8}");
            long count = Marshal.ReadInt64(buffer);
            int size = Marshal.SizeOf<HandleEntry>();
            if (count < 0 || count > (length - 16) / size) throw new InvalidDataException("Handle count exceeds snapshot buffer.");
            var entries = new List<HandleEntry>((int)count);
            for (int index = 0; index < count; index++)
                entries.Add(Marshal.PtrToStructure<HandleEntry>(buffer + 16 + index * size));
            return entries;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    throw new InvalidOperationException("Handle snapshot exceeded its bounded retry/buffer limit.");
}

static string TypeName(nint handle)
{
    int length = 1024;
    for (int attempt = 0; attempt < 4 && length <= 64 * 1024; attempt++)
    {
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            int status = Native.NtQueryObject(handle, 2, buffer, length, out int required);
            if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023) or unchecked((int)0x80000005))
            { length = Math.Max(length * 2, required + 64); continue; }
            if (status < 0) throw new InvalidOperationException($"ObjectTypeInformation NTSTATUS 0x{status:X8}");
            ushort bytes = unchecked((ushort)Marshal.ReadInt16(buffer));
            nint text = Marshal.ReadIntPtr(buffer, 8);
            if (bytes % 2 != 0 || text < buffer || text > buffer + length || text + bytes > buffer + length)
                throw new InvalidDataException("Object type name is outside its buffer.");
            return Marshal.PtrToStringUni(text, bytes / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    throw new InvalidOperationException("Object type query exceeded its bounded retry/buffer limit.");
}

[StructLayout(LayoutKind.Sequential)]
struct HandleEntry
{
    public nint HandleValue;
    public nuint HandleCount, PointerCount;
    public uint GrantedAccess, ObjectTypeIndex, HandleAttributes, Reserved;
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetProcessHandleCount(SafeProcessHandle process, out uint count);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DuplicateHandle(SafeProcessHandle source, nint original, nint target, out nint duplicate, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(nint handle);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, nint buffer, int size, out int required);
    [DllImport("ntdll.dll")] public static extern int NtQueryObject(nint handle, int informationClass, nint buffer, int size, out int required);
}
