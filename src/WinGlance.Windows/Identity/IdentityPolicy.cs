using WinGlance.Core;

namespace WinGlance.Windows.Identity;

/// <summary>Conservative, deterministic identity rules kept separate from Windows metadata I/O.</summary>
public static class IdentityPolicy
{
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "rundll32", "dllhost", "conhost", "taskhostw", "runtimebroker", "applicationframehost",
        "msedgewebview2", "webviewhost", "backgroundtaskhost", "wscript", "cscript", "powershell", "pwsh",
        "cmd", "python", "pythonw", "node", "java", "javaw", "dotnet", "wsl", "wslhost"
    };
    private static readonly HashSet<string> WindowsCoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "fontdrvhost", "dwm",
        "conhost", "taskhostw", "sihost", "ctfmon", "audiodg", "spoolsv", "wmiprvse", "dashost",
        "dllhost", "runtimebroker", "applicationframehost", "backgroundtaskhost", "searchprotocolhost",
        "searchfilterhost", "searchindexer", "securityhealthservice", "sgrmbroker", "wudfhost"
    };

    public static bool IsSharedHost(string processName) => SharedHosts.Contains(BaseName(processName));

    public static string GroupKey(int pid, long startTimeUtcTicks, string processName, string? filePath,
        int sessionId, string? packageIdentity = null)
    {
        if (!string.IsNullOrWhiteSpace(packageIdentity))
            return $"package:{sessionId}:{packageIdentity.ToUpperInvariant()}";
        if (IsSharedHost(processName) || string.IsNullOrWhiteSpace(filePath))
            return $"process:{sessionId}:{pid}:{startTimeUtcTicks}";
        var normalized = NormalizePath(filePath);
        return $"file:{sessionId}:{normalized}";
    }

    public static (SoftwareCategory Category, string Reason) Classify(string processName, string? filePath,
        string windowsDirectory, bool isService = false, bool microsoftSignatureVerified = false)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (SoftwareCategory.Unknown, "无法读取映像路径；保留显示");
        var path = NormalizePath(filePath);
        var windows = NormalizePath(windowsDirectory).TrimEnd('\\');
        var systemFolder = path.StartsWith(windows + "\\SYSTEM32\\", StringComparison.Ordinal)
            || path.StartsWith(windows + "\\SYSWOW64\\", StringComparison.Ordinal);
        if (systemFolder && WindowsCoreNames.Contains(BaseName(processName)) && microsoftSignatureVerified)
            return (SoftwareCategory.WindowsSystem, "Windows 系统目录、核心组件规则及已验证 Microsoft 签名");
        if (systemFolder && WindowsCoreNames.Contains(BaseName(processName)))
            return (SoftwareCategory.Unknown, "匹配 Windows 核心组件规则；签名尚未验证，保留显示");
        if (path.StartsWith(windows + "\\", StringComparison.Ordinal))
            return (SoftwareCategory.Unknown, "位于 Windows 目录，但不足以确认系统组件；保留显示");
        return isService
            ? (SoftwareCategory.ThirdPartyService, "服务管理器识别的非 Windows 系统服务")
            : (SoftwareCategory.Application, "应用程序；Microsoft 发行的普通应用仍显示");
    }

    public static string NormalizePath(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    private static string BaseName(string processName) => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? processName[..^4] : processName;
}
