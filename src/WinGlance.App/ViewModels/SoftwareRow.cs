using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinGlance.Core;

namespace WinGlance.App.ViewModels;

public static class Display
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    public static string Percent(double? value) => value is { } number && double.IsFinite(number) ? $"{number:F1}%" : "—";
    public static string Bytes(double? value)
    {
        if (value is null || !double.IsFinite(value.Value)) return "—";
        var number = Math.Max(0, value.Value);
        int i = 0;
        while (number >= 1024 && i < Units.Length - 1) { number /= 1024; i++; }
        return $"{number:0.#} {Units[i]}";
    }
    public static string Rate(double? value) => value is null ? "—" : Bytes(value) + "/s";
}

public sealed class SoftwareRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public SoftwareSample Sample { get; private set; }
    public ProcessSample? Process { get; private set; }
    public string Key { get; }
    public bool IsChild => Process is not null;

    private bool _initialized;
    private string? _iconPath;
    private BitmapImage? _icon;
    private string _name = "", _subtitle = "", _tooltip = "", _expandGlyph = "›";
    private string _cpu = "—", _gpu = "—", _memory = "—", _download = "—", _upload = "—";
    private string _diskRead = "读 —", _diskWrite = "写 —";
    private double? _cpuRaw, _gpuRaw, _memoryRaw, _downloadRaw, _uploadRaw, _diskReadRaw, _diskWriteRaw;
    private double _cpuValue, _gpuValue;
    private Visibility _expandVisibility = Visibility.Collapsed;
    private Thickness _indent;
    private Brush _cpuBrush = Heat(0, "#38B7A4"), _gpuBrush = Heat(0, "#8B83DC");

    public string Name => _name;
    public string Subtitle => _subtitle;
    public string Tooltip => _tooltip;
    public Thickness Indent => _indent;
    public Visibility ExpandVisibility => _expandVisibility;
    public string ExpandGlyph => _expandGlyph;
    public BitmapImage? Icon => _icon;
    public double CpuValue => _cpuValue;
    public double GpuValue => _gpuValue;
    public string Cpu => _cpu;
    public string Gpu => _gpu;
    public string Memory => _memory;
    public string Download => _download;
    public string Upload => _upload;
    public string DiskRead => _diskRead;
    public string DiskWrite => _diskWrite;
    public Brush CpuBrush => _cpuBrush;
    public Brush GpuBrush => _gpuBrush;

    public SoftwareRow(string key, SoftwareSample sample, ProcessSample? process = null)
    {
        Key = key;
        Sample = sample;
        Update(sample, process, false);
    }

    public void Update(SoftwareSample sample, ProcessSample? process, bool expanded)
    {
        var previousSample = Sample;
        var previousProcess = Process;
        bool isChild = process is not null;
        bool childChanged = isChild != (previousProcess is not null);
        string name = process?.Software.ProcessName ?? sample.Software.DisplayName;
        bool nameChanged = !string.Equals(_name, name, StringComparison.Ordinal);
        bool tooltipChanged = !_initialized || nameChanged ||
            !string.Equals(previousSample.Software.FilePath, sample.Software.FilePath, StringComparison.Ordinal) ||
            !SamePids(previousSample.Processes, sample.Processes);
        bool subtitleChanged = !_initialized || childChanged || (isChild
            ? previousProcess?.Identity.Pid != process!.Identity.Pid
            : previousSample.Processes.Count != sample.Processes.Count || previousSample.Software.Category != sample.Software.Category);
        Sample = sample;
        Process = process;

        Set(ref _name, name, Changes.Name);
        if (subtitleChanged)
            Set(ref _subtitle, process is { } child ? $"PID {child.Identity.Pid}" :
                $"{sample.Processes.Count} 个进程 · {CategoryName(sample.Software.Category)}", Changes.Subtitle);
        if (tooltipChanged)
            Set(ref _tooltip, $"{name}\n{sample.Software.FilePath ?? "路径不可访问"}\nPID: {string.Join(", ", sample.Processes.Select(p => p.Identity.Pid))}", Changes.Tooltip);
        if (!_initialized || childChanged)
        {
            Set(ref _indent, new Thickness(isChild ? 28 : 0, 0, 0, 0), Changes.Indent);
            if (childChanged) Notify(Changes.IsChild);
        }
        Set(ref _expandGlyph, expanded ? "⌄" : "›", Changes.ExpandGlyph);
        Set(ref _expandVisibility, !isChild && sample.Processes.Count > 1 ? Visibility.Visible : Visibility.Collapsed, Changes.ExpandVisibility);

        var iconPath = process?.Software.IconPath ?? sample.Software.IconPath;
        if (!string.Equals(_iconPath, iconPath, StringComparison.Ordinal))
        {
            _iconPath = iconPath;
            try { _icon = iconPath is null ? null : new BitmapImage(new Uri(iconPath)); } catch { _icon = null; }
            Notify(Changes.Icon);
        }

        double? cpu = isChild ? process!.CpuPercent : sample.CpuPercent;
        // Compute a child's engine maximum only once, sharing it between text and progress.
        double? gpuBar = isChild ? MaximumEngine(process!.GpuEngines) : sample.GpuPercent;
        double? gpuText = isChild && process!.GpuState != MetricState.Ready ? null : gpuBar;
        UpdateMetric(ref _cpuRaw, cpu, ref _cpu, Display.Percent, Changes.Cpu);
        UpdateMetric(ref _gpuRaw, gpuText, ref _gpu, Display.Percent, Changes.Gpu);
        UpdateMetric(ref _memoryRaw, isChild ? process!.PrivateWorkingSetBytes : sample.PrivateWorkingSetBytes, ref _memory, Display.Bytes, Changes.Memory);
        UpdateMetric(ref _downloadRaw, isChild ? process!.DownloadBytesPerSecond : sample.DownloadBytesPerSecond, ref _download, Display.Rate, Changes.Download);
        UpdateMetric(ref _uploadRaw, isChild ? process!.UploadBytesPerSecond : sample.UploadBytesPerSecond, ref _upload, Display.Rate, Changes.Upload);
        UpdateMetric(ref _diskReadRaw, isChild ? process!.DiskReadBytesPerSecond : sample.DiskReadBytesPerSecond, ref _diskRead,
            static value => "读 " + Display.Rate(value), Changes.DiskRead);
        UpdateMetric(ref _diskWriteRaw, isChild ? process!.DiskWriteBytesPerSecond : sample.DiskWriteBytesPerSecond, ref _diskWrite,
            static value => "写 " + Display.Rate(value), Changes.DiskWrite);
        if (Set(ref _cpuValue, Math.Round(cpu ?? 0, 1), Changes.CpuValue))
            Set(ref _cpuBrush, Heat(_cpuValue, "#38B7A4"), Changes.CpuBrush);
        if (Set(ref _gpuValue, Math.Round(gpuBar ?? 0, 1), Changes.GpuValue))
            Set(ref _gpuBrush, Heat(_gpuValue, "#8B83DC"), Changes.GpuBrush);
        _initialized = true;
    }

    private void UpdateMetric(ref double? previous, double? next, ref string display,
        Func<double?, string> formatter, PropertyChangedEventArgs change)
    {
        if (_initialized && Nullable.Equals(previous, next)) return;
        previous = next;
        // Preserve the formatter's exact rounding. A changed raw value need not change the label.
        Set(ref display, formatter(next), change);
    }

    private bool Set<T>(ref T field, T value, PropertyChangedEventArgs change)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(change);
        return true;
    }

    private static bool SamePids(IReadOnlyList<ProcessSample> previous, IReadOnlyList<ProcessSample> next)
    {
        if (ReferenceEquals(previous, next)) return true;
        if (previous.Count != next.Count) return false;
        for (int i = 0; i < previous.Count; i++)
            if (previous[i].Identity.Pid != next[i].Identity.Pid) return false;
        return true;
    }

    private static double MaximumEngine(IReadOnlyDictionary<string, double> engines)
    {
        if (engines.Count == 0) return 0;
        double maximum = double.NaN;
        foreach (double value in engines.Values)
            if (double.IsNaN(maximum) || value > maximum) maximum = value;
        return maximum;
    }

    private void Notify(PropertyChangedEventArgs change) => PropertyChanged?.Invoke(this, change);

    private static class Changes
    {
        public static readonly PropertyChangedEventArgs Name = new(nameof(SoftwareRow.Name));
        public static readonly PropertyChangedEventArgs Subtitle = new(nameof(SoftwareRow.Subtitle));
        public static readonly PropertyChangedEventArgs Tooltip = new(nameof(SoftwareRow.Tooltip));
        public static readonly PropertyChangedEventArgs Indent = new(nameof(SoftwareRow.Indent));
        public static readonly PropertyChangedEventArgs IsChild = new(nameof(SoftwareRow.IsChild));
        public static readonly PropertyChangedEventArgs ExpandVisibility = new(nameof(SoftwareRow.ExpandVisibility));
        public static readonly PropertyChangedEventArgs ExpandGlyph = new(nameof(SoftwareRow.ExpandGlyph));
        public static readonly PropertyChangedEventArgs Icon = new(nameof(SoftwareRow.Icon));
        public static readonly PropertyChangedEventArgs Cpu = new(nameof(SoftwareRow.Cpu));
        public static readonly PropertyChangedEventArgs CpuValue = new(nameof(SoftwareRow.CpuValue));
        public static readonly PropertyChangedEventArgs CpuBrush = new(nameof(SoftwareRow.CpuBrush));
        public static readonly PropertyChangedEventArgs Gpu = new(nameof(SoftwareRow.Gpu));
        public static readonly PropertyChangedEventArgs GpuValue = new(nameof(SoftwareRow.GpuValue));
        public static readonly PropertyChangedEventArgs GpuBrush = new(nameof(SoftwareRow.GpuBrush));
        public static readonly PropertyChangedEventArgs Memory = new(nameof(SoftwareRow.Memory));
        public static readonly PropertyChangedEventArgs Download = new(nameof(SoftwareRow.Download));
        public static readonly PropertyChangedEventArgs Upload = new(nameof(SoftwareRow.Upload));
        public static readonly PropertyChangedEventArgs DiskRead = new(nameof(SoftwareRow.DiskRead));
        public static readonly PropertyChangedEventArgs DiskWrite = new(nameof(SoftwareRow.DiskWrite));
    }

    private static readonly Dictionary<string, Brush> Brushes = [];
    private static Brush Heat(double value, string normal)
    {
        string hex = value > 80 ? "#E47768" : value > 50 ? "#D7A45B" : normal;
        if (!Brushes.TryGetValue(hex, out var brush)) Brushes[hex] = brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
        return brush;
    }
    public static string CategoryName(SoftwareCategory category) => category switch { SoftwareCategory.WindowsSystem => "Windows 系统", SoftwareCategory.ThirdPartyService => "后台服务", SoftwareCategory.Application => "应用", _ => "身份待确认" };
}
