using System.Runtime.InteropServices;

namespace WinGlance.Windows.Telemetry;

/// <summary>Adapter capacities only. Never uses process video-memory budgets as physical adapter usage.</summary>
internal sealed unsafe class DxgiAdapters
{
    private DateTimeOffset _lastRead;
    private IReadOnlyDictionary<string, AdapterCapacity> _cache = new Dictionary<string, AdapterCapacity>();

    internal IReadOnlyDictionary<string, AdapterCapacity> Read()
    {
        if (DateTimeOffset.UtcNow - _lastRead < TimeSpan.FromSeconds(30)) return _cache;
        _lastRead = DateTimeOffset.UtcNow;
        var result = new Dictionary<string, AdapterCapacity>(StringComparer.OrdinalIgnoreCase);
        nint factory = 0;
        try
        {
            var iid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"); // IDXGIFactory
            if (CreateDXGIFactory(ref iid, out factory) < 0 || factory == 0) return _cache = result;
            var table = *(nint**)factory;
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)table[7];
            for (uint index = 0; index < 64; index++)
            {
                nint adapter = 0;
                if (enumerate(factory, index, &adapter) < 0 || adapter == 0) break;
                try
                {
                    var adapterTable = *(nint**)adapter;
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)adapterTable[8];
                    AdapterDescription description = default;
                    if (getDescription(adapter, &description) < 0) continue;
                    string id = $"luid_0x{description.LuidHigh:x8}_0x{description.LuidLow:x8}";
                    string name = new((char*)description.Description);
                    result[id] = new(name, ToLong(description.DedicatedVideoMemory), ToLong(description.SharedSystemMemory));
                }
                finally { Release(adapter); }
            }
            return _cache = result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        { return _cache = result; }
        finally { if (factory != 0) Release(factory); }
    }

    private static long? ToLong(nuint value) => (ulong)value <= long.MaxValue ? (long)value : null;
    private static void Release(nint value) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)value)[2])(value);
    [StructLayout(LayoutKind.Sequential)] private struct AdapterDescription
    {
        public fixed ushort Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory(ref Guid iid, out nint factory);
}

internal sealed record AdapterCapacity(string Name, long? DedicatedBytes, long? SharedBytes);
