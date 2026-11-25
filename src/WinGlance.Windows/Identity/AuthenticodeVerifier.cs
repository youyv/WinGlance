using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace WinGlance.Windows.Identity;

[SupportedOSPlatform("windows")]
internal static class AuthenticodeVerifier
{
    private static readonly Guid VerifyAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    public static (bool MicrosoftVerified, string? Publisher) Verify(string path)
    {
        try
        {
            using var name = new NativeString(path);
            var file = new FileInfoNative { Size = (uint)Marshal.SizeOf<FileInfoNative>(), FilePath = name.Pointer };
            if (VerifyObject(file, 1)) return Publisher(path);
            // Most Windows components are catalog signed rather than embedded signed.
            using var input = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            (bool MicrosoftVerified, string? Publisher)? fallback = null;
            foreach (var algorithm in new[] { "SHA256", "SHA1" })
            {
                if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
                try
                {
                    uint length = 0;
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, input, ref length, null, 0) || length is 0 or > 128) continue;
                    var hash = new byte[length];
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, input, ref length, hash, 0)) continue;
                    using var memberTag = new NativeString(Convert.ToHexString(hash));
                    var pinnedHash = GCHandle.Alloc(hash, GCHandleType.Pinned);
                    try
                    {
                        var result = CatalogTrustSearch.Find(
                            previous => CryptCATAdminEnumCatalogFromHash(admin, hash, length, 0, ref previous),
                            catalog =>
                        {
                            var info = new CatalogInfo { Size = (uint)Marshal.SizeOf<CatalogInfo>(), CatalogFile = "" };
                            if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0)) return null;
                            using var catalogName = new NativeString(info.CatalogFile);
                            var member = new CatalogMember
                            {
                                Size = (uint)Marshal.SizeOf<CatalogMember>(), CatalogFile = catalogName.Pointer,
                                MemberTag = memberTag.Pointer, MemberFilePath = name.Pointer,
                                MemberFile = input.DangerousGetHandle(), CalculatedFileHash = pinnedHash.AddrOfPinnedObject(),
                                CalculatedFileHashSize = length, CatAdmin = admin
                            };
                            if (!VerifyObject(member, 2)) return null;
                            try { return Publisher(info.CatalogFile); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                            { return null; }
                        }, catalog => CryptCATAdminReleaseCatalogContext(admin, catalog, 0));
                        if (result is { MicrosoftVerified: true } trusted) return trusted;
                        fallback ??= result;
                    }
                    finally { pinnedHash.Free(); }
                }
                finally { CryptCATAdminReleaseContext(admin, 0); }
            }
            if (fallback is { } publisher) return publisher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
        return (false, null);
    }

    private static (bool MicrosoftVerified, string? Publisher) Publisher(string signedFile)
    {
#pragma warning disable SYSLIB0057
        using var signedCertificate = X509Certificate.CreateFromSignedFile(signedFile);
        using var certificate = new X509Certificate2(signedCertificate);
#pragma warning restore SYSLIB0057
        var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
        // The certificate is read only after WinVerifyTrust validated the signed object.
        var microsoft = publisher is "Microsoft Corporation" or "Microsoft Windows" or "Microsoft Windows Publisher";
        return (microsoft, publisher);
    }

    private static bool VerifyObject<T>(T obj, uint choice) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(obj, pointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = choice,
                UnionPointer = pointer, StateAction = 1,
                // No network fetching on the monitoring hot path; use locally available trust data.
                ProviderFlags = 0x1000 | 0x10
            };
            var action = VerifyAction;
            try { return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0; }
            finally { data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private sealed class NativeString(string value) : IDisposable
    {
        public IntPtr Pointer { get; } = Marshal.StringToCoTaskMemUni(value);
        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative { public uint Size; public IntPtr FilePath, File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallbackData, SipClientData; public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr UnionPointer; public uint StateAction; public IntPtr StateData, UrlReference; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CatalogMember
    {
        public uint Size, CatalogVersion; public IntPtr CatalogFile, MemberTag, MemberFilePath, MemberFile,
            CalculatedFileHash; public uint CalculatedFileHashSize; public IntPtr CatalogContext, CatAdmin;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct CatalogInfo
    {
        public uint Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string CatalogFile;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string algorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, SafeFileHandle file, ref uint hashSize, byte[]? hash, uint flags);
    [DllImport("wintrust.dll")] private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint hashSize, uint flags, ref IntPtr previous);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);
    [DllImport("wintrust.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
