using WinGlance.Core;

namespace WinGlance.App.ViewModels;

internal static class ProcessPresentation
{
    internal static string Tooltip(string name, SoftwareSample sample, ProcessSample? process) =>
        $"{name}\n{(process is null ? sample.Software.FilePath : process.Software.FilePath) ?? "路径不可访问"}\nPID: " +
        (process is null ? string.Join(", ", sample.Processes.Select(p => p.Identity.Pid)) : process.Identity.Pid.ToString());
}
