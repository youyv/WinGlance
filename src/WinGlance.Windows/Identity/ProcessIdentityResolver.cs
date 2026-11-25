using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinGlance.Core;

namespace WinGlance.Windows.Identity;

/// <summary>Returns immediate identities while a bounded worker resolves expensive file metadata.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessIdentityResolver : IProcessIdentityResolver, IDisposable
{
    private const int MaximumEntries = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Metadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProcessIdentity, PackageCacheEntry> _packages = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<MetadataWork> _work = new(256);
    private readonly Thread _worker;
    private readonly TimeProvider _timeProvider;
    private readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private HashSet<int> _serviceProcesses = [];
    private long _servicesReadUtcTicks;
    private bool _disposed;

    public ProcessIdentityResolver(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worker = new Thread(ProcessQueue) { IsBackground = true, Name = "WinGlance identity metadata" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public SoftwareIdentity Resolve(int pid, string processName, string? filePath, int sessionId, long startTimeUtcTicks = 0)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        var validPath = CanonicalPath(filePath);
        var package = ResolvePackage(pid, startTimeUtcTicks);
        var cacheKey = package is null ? validPath : validPath + "|" + package.FullName + "|" + package.ApplicationId;
        Metadata? metadata = null;
        if (validPath is not null && cacheKey is not null)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    if (_metadata.TryGetValue(cacheKey, out metadata)) metadata.LastAccess = Stopwatch.GetTimestamp();
                    // Refresh metadata periodically so executable replacement invalidates icons and identity.
                    if ((metadata is null || UtcNow - metadata.LastCheckedUtc > TimeSpan.FromMinutes(2))
                        && _queued.Add(cacheKey))
                    {
                        if (!_work.TryAdd(new MetadataWork(cacheKey, validPath, package))) _queued.Remove(cacheKey);
                    }
                }
            }
        }
        bool isService;
        lock (_gate) isService = startTimeUtcTicks > 0 && startTimeUtcTicks <= _servicesReadUtcTicks && _serviceProcesses.Contains(pid);
        var (category, reason) = IdentityPolicy.Classify(name, validPath, _windows, isService,
            microsoftSignatureVerified: metadata?.MicrosoftVerified == true);
        if (package is not null)
        {
            category = SoftwareCategory.Application;
            reason = "已由进程句柄验证包与应用身份；保留已打包应用及其运行时显示";
        }
        var key = IdentityPolicy.GroupKey(pid, startTimeUtcTicks, name, validPath, sessionId,
            package is null ? null : package.Family + "|" + package.ApplicationId);
        return new SoftwareIdentity(key, metadata?.DisplayName ?? name, processName, validPath,
            metadata?.Publisher, category, reason, metadata?.IconPath);
    }

    private PackageIdentity? ResolvePackage(int pid, long startTimeUtcTicks)
    {
        // Creation-time keyed caching prevents a recycled PID inheriting another app's package.
        var identity = new ProcessIdentity(pid, startTimeUtcTicks);
        lock (_gate)
        {
            if (_disposed) return null;
            if (startTimeUtcTicks != 0 && _packages.TryGetValue(identity, out var cached))
            {
                cached.LastAccess = Stopwatch.GetTimestamp();
                return cached.Package;
            }
        }
        var package = PackageIdentityReader.Read(pid, startTimeUtcTicks);
        if (startTimeUtcTicks != 0)
        {
            lock (_gate)
            {
                if (_disposed) return package;
                _packages[identity] = new PackageCacheEntry(package);
                if (_packages.Count > MaximumEntries * 2)
                    _packages.Remove(_packages.MinBy(pair => pair.Value.LastAccess).Key);
            }
        }
        return package;
    }

    private void ProcessQueue()
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 2) >= 0;
        try
        {
            while (!_work.IsCompleted)
            {
                lock (_gate) if (_disposed) break;
                if (DateTime.UtcNow.Ticks - _servicesReadUtcTicks > TimeSpan.FromSeconds(15).Ticks)
                {
                    var capturedAt = DateTime.UtcNow.Ticks;
                    var services = ServiceProcessReader.Read();
                    lock (_gate) { _serviceProcesses = services; _servicesReadUtcTicks = capturedAt; }
                }
                if (!_work.TryTake(out var work, 1000)) continue;
                var checkedAt = UtcNow;
                var path = work.FilePath;
                Metadata? previous;
                lock (_gate) _metadata.TryGetValue(work.CacheKey, out previous);
                Metadata result;
                try
                {
                    var file = new FileInfo(path);
                    var existed = file.Exists;
                    var modifiedTicks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
                    var createdTicks = file.Exists ? file.CreationTimeUtc.Ticks : 0;
                    var length = file.Exists ? file.Length : 0;
                    if (!file.Exists) result = new Metadata { LastCheckedUtc = checkedAt };
                    else if (previous is not null && previous.ModifiedTicks == modifiedTicks
                        && previous.CreatedTicks == createdTicks && previous.Length == length)
                    {
                        result = previous.Copy();
                        if (result.IconPath is null || !File.Exists(result.IconPath))
                            result.IconPath = ExtractIcon(work, modifiedTicks, length, createdTicks);
                        result.LastCheckedUtc = checkedAt;
                    }
                    else
                    {
                        var version = FileVersionInfo.GetVersionInfo(path);
                        var signature = AuthenticodeVerifier.Verify(path);
                        var packageName = work.Package is null ? null : PackageIdentityReader.DisplayName(work.Package.ApplicationId);
                        result = new Metadata
                        {
                            DisplayName = Clean(packageName) ?? Clean(version.FileDescription) ?? Clean(version.ProductName),
                            Publisher = signature.Publisher ?? Clean(version.CompanyName),
                            MicrosoftVerified = signature.MicrosoftVerified,
                            ModifiedTicks = modifiedTicks, CreatedTicks = createdTicks, Length = length,
                            IconPath = ExtractIcon(work, modifiedTicks, length, createdTicks),
                            LastCheckedUtc = checkedAt
                        };
                    }
                    file.Refresh();
                    if (existed && (!file.Exists || file.LastWriteTimeUtc.Ticks != modifiedTicks || file.CreationTimeUtc.Ticks != createdTicks || file.Length != length))
                        result = new Metadata(); // The executable changed during extraction; retry without publishing mixed metadata.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                    or ArgumentException or COMException or System.Security.Cryptography.CryptographicException)
                { result = new Metadata { LastCheckedUtc = checkedAt }; }
                lock (_gate)
                {
                    if (_disposed) break;
                    result.LastAccess = Stopwatch.GetTimestamp();
                    _metadata[work.CacheKey] = result;
                    _queued.Remove(work.CacheKey);
                    if (_metadata.Count > MaximumEntries)
                        _metadata.Remove(_metadata.MinBy(pair => pair.Value.LastAccess).Key);
                }
            }
        }
        finally { if (initialized) CoUninitialize(); }
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private static string? ExtractIcon(MetadataWork work, long modifiedTicks, long length, long createdTicks)
    {
        var packageIcon = work.Package is null ? null
            : ShellIconCache.Extract("shell:AppsFolder\\" + work.Package.ApplicationId, modifiedTicks, length, createdTicks);
        return packageIcon ?? ShellIconCache.Extract(work.FilePath, modifiedTicks, length, createdTicks);
    }

    private static string? Clean(string? value)
    {
        var clean = value?.Trim('\0', ' ', '\r', '\n', '\t');
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string? CanonicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith("\\\\?\\", StringComparison.Ordinal) ? full[4..] : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _work.CompleteAdding();
            _metadata.Clear();
            _packages.Clear();
            _serviceProcesses.Clear();
            _queued.Clear();
        }
        // Shell extensions can be slow; do not block application shutdown indefinitely.
        if (_worker.Join(TimeSpan.FromSeconds(2))) _work.Dispose();
    }

    private sealed class Metadata
    {
        public string? DisplayName, Publisher, IconPath;
        public bool MicrosoftVerified;
        public long ModifiedTicks, CreatedTicks, Length, LastAccess;
        public DateTime LastCheckedUtc;
        public Metadata Copy() => (Metadata)MemberwiseClone();
    }

    private sealed record MetadataWork(string CacheKey, string FilePath, PackageIdentity? Package);
    private sealed class PackageCacheEntry(PackageIdentity? package)
    {
        public PackageIdentity? Package { get; } = package;
        public long LastAccess = Stopwatch.GetTimestamp();
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
