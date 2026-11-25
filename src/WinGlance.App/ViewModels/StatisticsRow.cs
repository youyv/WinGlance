using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;
using WinGlance.Core;
using WinGlance.Statistics;

namespace WinGlance.App.ViewModels;

public static class StatisticsDisplay
{
    public static MetricStatistics? Get(IReadOnlyDictionary<StatisticMetric, MetricStatistics> metrics, StatisticMetric key) => metrics.GetValueOrDefault(key);
    public static double? Total(IReadOnlyDictionary<StatisticMetric, MetricStatistics> metrics, StatisticMetric a, StatisticMetric b) =>
        Get(metrics, a)?.Bytes is { } av && Get(metrics, b)?.Bytes is { } bv ? (double)av + bv : null;
    public static string Duration(double seconds) => seconds >= 86400 ? $"{seconds / 86400:0.#} 天" : seconds >= 3600 ? $"{seconds / 3600:0.#} 小时" : seconds >= 60 ? $"{seconds / 60:0.#} 分钟" : $"{Math.Max(0, seconds):0.#} 秒";
    public static string CpuTime(long? ticks) => ticks is { } value ? Duration((double)value / TimeSpan.TicksPerSecond) : "—";
    public static string Coverage(MetricStatistics? metric) => metric?.ValidSeconds > 0 ? $"覆盖 {Math.Clamp(metric.Coverage, 0, 1):P0}" : metric?.Bytes is not null || metric?.CpuTicks is not null ? "部分已观测 · 覆盖不足" : "尚无有效观测";
    public static string PeakTime(MetricStatistics? metric) => metric?.PeakUtc is { } time ? $" · {time.LocalDateTime:MM-dd HH:mm:ss}" : "";
}

public sealed class StatisticsRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Key { get; }
    public ApplicationStatistics Sample { get; private set; }
    private readonly Dictionary<string, string> _texts = [];
    private string? _iconPath;
    public BitmapImage? Icon { get; private set; }
    public double Bar { get; private set; }
    public string Name => Text();
    public string Subtitle => Text();
    public string Tooltip => Text();
    public string First => Text();
    public string Second => Text();
    public string Third => Text();
    public string Fourth => Text();
    public string FirstHint => Text();
    public string SecondHint => Text();
    public string ThirdHint => Text();
    public string FourthHint => Text();
    private string Text([CallerMemberName] string key = "") => _texts.GetValueOrDefault(key, "");
    private void Set(string key, string value)
    {
        if (_texts.GetValueOrDefault(key) == value) return;
        _texts[key] = value; PropertyChanged?.Invoke(this, new(key));
    }
    public StatisticsRow(ApplicationStatistics sample) { Key = sample.AppId; Sample = sample; }
    public static double? SortValue(ApplicationStatistics app, string view, string column)
    {
        var m = app.Metrics;
        double? Network() => StatisticsDisplay.Total(m, StatisticMetric.Download, StatisticMetric.Upload);
        double? Disk() => StatisticsDisplay.Total(m, StatisticMetric.DiskRead, StatisticMetric.DiskWrite);
        return (view, column) switch
        {
            ("network", "second") => StatisticsDisplay.Get(m, StatisticMetric.Download)?.Bytes,
            ("network", "third") => StatisticsDisplay.Get(m, StatisticMetric.Upload)?.Bytes,
            ("network", _) => Network(),
            ("disk", "second") => StatisticsDisplay.Get(m, StatisticMetric.DiskRead)?.Bytes,
            ("disk", "third") => StatisticsDisplay.Get(m, StatisticMetric.DiskWrite)?.Bytes,
            ("disk", _) => Disk(),
            ("compute", "second") => StatisticsDisplay.Get(m, StatisticMetric.Gpu)?.Average,
            ("compute", "third") => StatisticsDisplay.Get(m, StatisticMetric.Memory)?.Peak,
            ("compute", "fourth") => StatisticsDisplay.Get(m, StatisticMetric.DedicatedGpuMemory)?.Peak,
            ("overview", "second") => Network(),
            ("overview", "third") => Disk(),
            ("overview", "fourth") => StatisticsDisplay.Get(m, StatisticMetric.Memory)?.Peak,
            _ => StatisticsDisplay.Get(m, StatisticMetric.Cpu)?.CpuTicks
        };
    }
    public void Update(ApplicationStatistics app, string view, double denominator)
    {
        Sample = app;
        var m = app.Metrics;
        var cpu = StatisticsDisplay.Get(m, StatisticMetric.Cpu);
        var gpu = StatisticsDisplay.Get(m, StatisticMetric.Gpu);
        var memory = StatisticsDisplay.Get(m, StatisticMetric.Memory);
        var vram = StatisticsDisplay.Get(m, StatisticMetric.DedicatedGpuMemory);
        Set(nameof(Name), app.Software.DisplayName);
        Set(nameof(Subtitle), $"{(app.IsRunning ? "运行中" : "已退出")} · 已观测 {StatisticsDisplay.Duration(app.ObservedSeconds)}");
        Set(nameof(Tooltip), $"{app.Software.DisplayName}\n{app.Software.FilePath ?? "路径不可访问"}\n{SoftwareRow.CategoryName(app.Software.Category)} · CPU {StatisticsDisplay.Coverage(cpu)}");
        if (_iconPath != app.Software.IconPath)
        {
            _iconPath = app.Software.IconPath;
            try { Icon = _iconPath is null ? null : new BitmapImage(new Uri(_iconPath)) { DecodePixelWidth = 40 }; } catch { Icon = null; }
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
        }
        string first, second, third, fourth, fh = "", sh = "", th = "", qh = "";
        double value = SortValue(app, view, "first") ?? 0;
        if (view is "network" or "disk")
        {
            var a = StatisticsDisplay.Get(m, view == "network" ? StatisticMetric.Download : StatisticMetric.DiskRead);
            var b = StatisticsDisplay.Get(m, view == "network" ? StatisticMetric.Upload : StatisticMetric.DiskWrite);
            first = Display.Bytes(SortValue(app, view, "first")); second = Display.Bytes(a?.Bytes); third = Display.Bytes(b?.Bytes);
            fourth = a?.Bytes is not null && b?.Bytes is not null && denominator > 0 ? $"{value / denominator:P1}" : "—";
            fh = a?.Bytes is not null || b?.Bytes is not null ? a?.ValidSeconds > 0 && b?.ValidSeconds > 0 ? $"覆盖 {Math.Min(a.Coverage, b.Coverage):P0}" : "部分已观测 · 覆盖不足" : "开启增强采集";
            qh = view == "network" ? "占全部可归属流量" : "占全部可归属吞吐";
        }
        else if (view == "compute")
        {
            first = StatisticsDisplay.CpuTime(cpu?.CpuTicks); fh = $"平均 {Display.Percent(cpu?.Average)}";
            second = Display.Percent(gpu?.Average); sh = $"峰值 {Display.Percent(gpu?.Peak)}";
            third = Display.Bytes(memory?.Peak); th = "私有工作集峰值";
            fourth = Display.Bytes(vram?.Peak); qh = $"共享 {Display.Bytes(StatisticsDisplay.Get(m, StatisticMetric.SharedGpuMemory)?.Peak)}";
        }
        else
        {
            first = StatisticsDisplay.CpuTime(cpu?.CpuTicks); fh = $"运行平均 {Display.Percent(cpu?.Average)}";
            second = Display.Bytes(StatisticsDisplay.Total(m, StatisticMetric.Download, StatisticMetric.Upload));
            third = Display.Bytes(StatisticsDisplay.Total(m, StatisticMetric.DiskRead, StatisticMetric.DiskWrite));
            fourth = Display.Bytes(memory?.Peak);
            sh = StatisticsDisplay.Get(m, StatisticMetric.Download)?.Bytes is not null ? StatisticsDisplay.Coverage(StatisticsDisplay.Get(m, StatisticMetric.Download)) : "开启增强采集";
            th = StatisticsDisplay.Get(m, StatisticMetric.DiskRead)?.Bytes is not null ? StatisticsDisplay.Coverage(StatisticsDisplay.Get(m, StatisticMetric.DiskRead)) : "开启增强采集";
            qh = "私有工作集";
        }
        Set(nameof(First), first); Set(nameof(Second), second); Set(nameof(Third), third); Set(nameof(Fourth), fourth);
        Set(nameof(FirstHint), fh); Set(nameof(SecondHint), sh); Set(nameof(ThirdHint), th); Set(nameof(FourthHint), qh);
        var bar = denominator > 0 ? Math.Round(Math.Clamp(value / denominator * 100, 0, 100), 1) : 0;
        if (Bar != bar) { Bar = bar; PropertyChanged?.Invoke(this, new(nameof(Bar))); }
    }
}
