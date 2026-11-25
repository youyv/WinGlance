using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinGlance.App.ViewModels;
using WinGlance.Core;
using WinGlance.Statistics;

namespace WinGlance.App.Controls;

public sealed partial class StatisticsView : UserControl
{
    private readonly StatisticsService _service;
    private readonly ObservableCollection<StatisticsRow> _rows = [];
    private readonly DispatcherQueueTimer _timer;
    private StatisticsSnapshot? _snapshot;
    private bool _ready, _active, _refreshing, _refreshAgain, _resetting, _descending = true;
    private int _version;
    private string _view = "overview", _sort = "first";
    private string? _selectedId;
    private DateTimeOffset _nextReorder, _nextDetail;
    public event Action<bool>? HideSystemChanged;
    public event Action? EnhancedRequested;
    public Func<StatisticsRange, Task<bool>>? ConfirmReset { get; set; }
    private StatisticsRange Range => (StatisticsRange)Math.Max(0, RangePicker.SelectedIndex);

    public StatisticsView(StatisticsService service, bool hideSystem)
    {
        _service = service;
        InitializeComponent();
        StatsHideSystem.IsOn = hideSystem;
        RankingList.ItemsSource = _rows;
        RankingList.ContainerContentChanging += (_, args) => ListViewMotion.Reset(args.ItemContainer);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Refresh();
        _ready = true;
        UpdateHeaders();
    }
    public void SetActive(bool active)
    {
        _active = active;
        if (active) { _timer.Start(); Refresh(); } else { _timer.Stop(); _version++; }
    }
    public void SetHideSystem(bool value)
    {
        if (StatsHideSystem.IsOn == value) return;
        StatsHideSystem.IsOn = value;
    }
    public void SetCollectionState(string label, bool enabled, string description)
    {
        StatsEnhancedButton.Content = label;
        StatsEnhancedButton.IsEnabled = enabled;
        ToolTipService.SetToolTip(StatsEnhancedButton, description);
    }
    private async void Refresh()
    {
        if (!_active || _resetting) return;
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true; int version = _version;
        try
        {
            var snapshot = await _service.QueryAsync(Range);
            if (!_active || version != _version) return;
            _snapshot = snapshot;
            RenderSummary(snapshot);
            RefreshRows(false);
            RenderTrend();
            if (_selectedId is not null && DateTimeOffset.UtcNow >= _nextDetail)
            {
                _nextDetail = DateTimeOffset.UtcNow.AddSeconds(5);
                await UpdateDetailAsync(version);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_active) { Notice.Title = "统计暂不可用"; Notice.Message = ex.Message; Notice.Severity = InfoBarSeverity.Error; Notice.IsOpen = true; }
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain) { _refreshAgain = false; Refresh(); }
        }
    }
    private void RenderSummary(StatisticsSnapshot snapshot)
    {
        var m = snapshot.System.Metrics;
        MetricStatistics? Get(StatisticMetric key) => m.GetValueOrDefault(key);
        var down = Get(StatisticMetric.Download); var up = Get(StatisticMetric.Upload);
        var read = Get(StatisticMetric.DiskRead); var write = Get(StatisticMetric.DiskWrite);
        var cpu = Get(StatisticMetric.Cpu); var gpu = Get(StatisticMetric.Gpu);
        var memory = Get(StatisticMetric.Memory); var vram = Get(StatisticMetric.DedicatedGpuMemory);
        ObservationLabel.Text = $"{snapshot.StartUtc.LocalDateTime:MM-dd HH:mm} 至 {snapshot.EndUtc.LocalDateTime:MM-dd HH:mm} · 已观测 {StatisticsDisplay.Duration(snapshot.System.ObservedSeconds)}";
        NetworkTotal.Text = Display.Bytes(StatisticsDisplay.Total(m, StatisticMetric.Download, StatisticMetric.Upload));
        NetworkParts.Text = $"↓ {Display.Bytes(down?.Bytes)}    ↑ {Display.Bytes(up?.Bytes)}";
        NetworkCoverage.Text = StatisticsDisplay.Coverage(down) + " · 物理接口合计";
        ToolTipService.SetToolTip(NetworkTotal, snapshot.NetworkScope ?? "累计网卡字节增量；应用流量采用不同口径，不要求与整机合计相等。");
        DiskTotal.Text = Display.Bytes(StatisticsDisplay.Total(m, StatisticMetric.DiskRead, StatisticMetric.DiskWrite));
        DiskParts.Text = $"读取 {Display.Bytes(read?.Bytes)}    写入 {Display.Bytes(write?.Bytes)}";
        DiskCoverage.Text = StatisticsDisplay.Coverage(read);
        CpuAverage.Text = Display.Percent(cpu?.Average); CpuPeak.Text = "采样峰值 " + Display.Percent(cpu?.Peak); CpuCoverage.Text = StatisticsDisplay.Coverage(cpu);
        GpuAverage.Text = Display.Percent(gpu?.Average); GpuPeak.Text = "采样峰值 " + Display.Percent(gpu?.Peak); GpuCoverage.Text = StatisticsDisplay.Coverage(gpu) + " · 最忙引擎";
        MemoryAverage.Text = Display.Bytes(memory?.Average);
        MemoryPeak.Text = "采样峰值 " + Display.Bytes(memory?.Peak) + CapacityPercent(memory?.Peak, snapshot.TotalMemoryBytes);
        MemoryCoverage.Text = StatisticsDisplay.Coverage(memory) + CapacityPercent(memory?.Average, snapshot.TotalMemoryBytes, " · 平均 ");
        VramAverage.Text = Display.Bytes(vram?.Average);
        VramPeak.Text = "采样峰值 " + Display.Bytes(vram?.Peak) + CapacityPercent(vram?.Peak, snapshot.DedicatedGpuCapacityBytes);
        VramCoverage.Text = StatisticsDisplay.Coverage(vram) + CapacityPercent(vram?.Average, snapshot.DedicatedGpuCapacityBytes, " · 平均 ");
        foreach (var (element, metric) in new[] { (CpuPeak, cpu), (GpuPeak, gpu), (MemoryPeak, memory), (VramPeak, vram) })
            ToolTipService.SetToolTip(element, "一秒采样峰值" + StatisticsDisplay.PeakTime(metric));
        var partial = snapshot.Applications.Any(a => a.Metrics.GetValueOrDefault(StatisticMetric.Download)?.Bytes is not null);
        string? notice = snapshot.Warning;
        if (!partial) notice = Join(notice, "单应用网络与磁盘需要开启增强采集；整机统计和 CPU / 内存已正常累计。");
        else if (snapshot.Applications.Any(a => a.Metrics.GetValueOrDefault(StatisticMetric.Download)?.Coverage < .99))
            notice = Join(notice, "应用流量仅覆盖增强采集可用的时段；过去未采集的数据无法补回。");
        if (snapshot.LastRecordedUtc is { } last && DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(6))
            notice = Join(notice, "采集暂未更新，累计值保留至最后一次有效观测。");
        Notice.Title = "统计覆盖"; Notice.Message = notice ?? ""; Notice.Severity = snapshot.Warning is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning; Notice.IsOpen = !string.IsNullOrEmpty(notice);
        SaveStatus.Text = (snapshot.LastSavedUtc is { } saved ? $"已保存 {saved.LocalDateTime:HH:mm:ss}" : "等待首次保存") + " · 每 30 秒保存 · 历史保留 7 天" + (snapshot.LastResetUtc is { } reset ? $" · 上次重置 {reset.LocalDateTime:MM-dd HH:mm:ss}" : "");
        string quality = "仅统计 WinGlance 运行期间；系统筛选不改变整机统计及排行占比分母。";
        if (snapshot.EtwQuality is { } q)
        {
            quality += $"\n未归属：下载 {Display.Bytes(q.UnattributedDownloadBytes)} / 上传 {Display.Bytes(q.UnattributedUploadBytes)} / 读 {Display.Bytes(q.UnattributedDiskReadBytes)} / 写 {Display.Bytes(q.UnattributedDiskWriteBytes)}。";
            if (q.LostEvents > 0) quality += $" ETW 丢失 {q.LostEvents:N0} 个事件，流量不完整，损失字节数未知。";
        }
        quality += "\n网络整机与应用口径不同；专用 / 共享显存分别统计，应用显存可能含重复映射。";
        QualityLabel.Text = quality;
    }
    private static string Join(string? first, string second) => string.IsNullOrEmpty(first) ? second : first + "\n" + second;
    private static string CapacityPercent(double? value, long? capacity, string prefix = " · ") => value is { } v && capacity > 0 ? prefix + $"{v / capacity:P1}" : "";

    private void RenderTrend()
    {
        if (_snapshot is not { } snapshot) return;
        var metric = TrendPicker.SelectedIndex switch { 1 => StatisticMetric.DiskRead, 2 => StatisticMetric.Cpu, 3 => StatisticMetric.Gpu, 4 => StatisticMetric.Memory, 5 => StatisticMetric.DedicatedGpuMemory, _ => StatisticMetric.Download };
        StatisticMetric? second = metric == StatisticMetric.Download ? StatisticMetric.Upload : metric == StatisticMetric.DiskRead ? StatisticMetric.DiskWrite : null;
        var start = snapshot.StartUtc > snapshot.EndUtc.AddDays(-7) ? snapshot.StartUtc : snapshot.EndUtc.AddDays(-7);
        TrendHint.Text = (second is null ? "有效时段平均值" : "每柱为时段累计量") + " · 空白表示未观测" + (start > snapshot.StartUtc ? " · 仅展示最近 7 天" : "");
        TrendChart.SetSeries(snapshot.SystemTrend, start, snapshot.EndUtc, metric, second);
    }
    private void RefreshRows(bool force = true)
    {
        if (!_ready || _snapshot is not { } snapshot) return;
        string query = StatsSearch.Text.Trim();
        var positions = _rows.Select((row, index) => (row.Key, index)).ToDictionary(x => x.Key, x => x.index);
        var all = snapshot.Applications;
        var filtered = all.Where(a => (!StatsHideSystem.IsOn || a.Software.Category != SoftwareCategory.WindowsSystem) &&
            (query.Length == 0 || a.Software.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || a.Software.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) || (a.Software.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)));
        bool reorder = force || DateTimeOffset.UtcNow >= _nextReorder;
        IEnumerable<ApplicationStatistics> ordered;
        if (!reorder) ordered = filtered.OrderBy(a => positions.GetValueOrDefault(a.AppId, int.MaxValue));
        else if (_sort == "name") ordered = _descending ? filtered.OrderByDescending(a => a.Software.DisplayName) : filtered.OrderBy(a => a.Software.DisplayName);
        else
        {
            var hasValue = filtered.OrderBy(a => StatisticsRow.SortValue(a, _view, _sort) is null);
            ordered = _descending ? hasValue.ThenByDescending(a => StatisticsRow.SortValue(a, _view, _sort)).ThenBy(a => positions.GetValueOrDefault(a.AppId, int.MaxValue))
                : hasValue.ThenBy(a => StatisticsRow.SortValue(a, _view, _sort)).ThenBy(a => positions.GetValueOrDefault(a.AppId, int.MaxValue));
        }
        var desired = ordered.ToArray();
        if (reorder) _nextReorder = DateTimeOffset.UtcNow.AddSeconds(5);
        double denominator = _view is "network" or "disk" ? all.Sum(a => StatisticsRow.SortValue(a, _view, "first") ?? 0) : all.Max(a => StatisticsRow.SortValue(a, _view, "first")) ?? 0;
        bool changed = !_rows.Select(r => r.Key).SequenceEqual(desired.Select(a => a.AppId));
        var previous = changed ? ListViewMotion.Capture(RankingList) : null;
        var keys = desired.Select(a => a.AppId).ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--) if (!keys.Contains(_rows[i].Key)) _rows.RemoveAt(i);
        var cached = _rows.ToDictionary(r => r.Key);
        for (int i = 0; i < desired.Length; i++)
        {
            var app = desired[i];
            if (!cached.TryGetValue(app.AppId, out var row)) { row = new StatisticsRow(app); _rows.Insert(i, row); }
            else
            {
                int old = i < _rows.Count && ReferenceEquals(_rows[i], row) ? i : _rows.IndexOf(row);
                if (old != i) { _rows.RemoveAt(old); _rows.Insert(i, row); }
            }
            row.Update(app, _view, denominator);
        }
        if (changed)
        {
            RankingList.SelectedItem = _rows.FirstOrDefault(r => r.Key == _selectedId);
            ListViewMotion.Animate(RankingList, previous);
        }
        ApplicationCount.Text = $"{desired.Length} 个应用 · 包含已退出";
        EmptyLabel.Text = all.Count == 0 ? "等待第一份有效统计数据…" : "没有匹配的应用";
        EmptyLabel.Visibility = desired.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateHeaders()
    {
        string[] labels = _view switch
        {
            "network" => ["总流量", "下载量", "上传量", "流量占比"],
            "disk" => ["总吞吐", "读取量", "写入量", "吞吐占比"],
            "compute" => ["CPU 耗时 / 平均", "GPU 平均 / 峰值", "内存峰值", "专用显存峰值"],
            _ => ["CPU 累计耗时", "网络流量", "磁盘吞吐", "内存峰值"]
        };
        (Button Button, string Label, string Key)[] headers = [(AppHeader, "应用", "name"), (FirstHeader, labels[0], "first"), (SecondHeader, labels[1], "second"), (ThirdHeader, labels[2], "third"), (FourthHeader, labels[3], "fourth")];
        foreach (var (button, label, key) in headers)
        {
            button.Content = label + (_sort == key ? _descending ? " ↓" : " ↑" : "");
            ToolTipService.SetToolTip(button, _sort == key ? $"当前{(_descending ? "降序" : "升序")}；点击切换方向" : "点击排序");
        }
        RankingHint.Text = _view switch
        {
            "network" => "占比以全部可归属应用流量为分母，搜索及隐藏系统进程不会改变分母。",
            "disk" => "占比以全部可归属应用磁盘吞吐为分母；普通采集不提供单应用磁盘历史。",
            "compute" => "平均值按运行期间的有效观测加权；缺测不当作零。峰值先聚合同时运行的子进程。条形对比最高 CPU 耗时。",
            _ => "综合视角默认按 CPU 累计耗时排序；条形对比最高 CPU 耗时。自动排序间隔 5 秒。"
        };
    }
    private void Range_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _version++; _nextReorder = default; _nextDetail = default; Refresh();
    }
    private void Trend_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) RenderTrend(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) => RefreshRows();
    private void HideSystem_Toggled(object sender, RoutedEventArgs e) { if (!_ready) return; HideSystemChanged?.Invoke(StatsHideSystem.IsOn); RefreshRows(); }
    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        _view = (string)((ToggleButton)sender).Tag; _sort = "first"; _descending = true;
        foreach (var tab in new[] { OverviewTab, NetworkTab, ComputeTab, DiskTab }) tab.IsChecked = (string)tab.Tag == _view;
        UpdateHeaders(); RefreshRows();
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        string sort = (string)((Button)sender).Tag;
        _descending = _sort == sort ? !_descending : sort != "name"; _sort = sort;
        UpdateHeaders(); RefreshRows();
    }
    private async void Application_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not StatisticsRow row) return;
        _selectedId = row.Key; DetailIcon.Source = row.Icon; HistoryDetail.Visibility = Visibility.Visible;
        await UpdateDetailAsync(_version);
    }
    private async Task UpdateDetailAsync(int version)
    {
        if (_selectedId is not { } id || _snapshot is not { } snapshot) return;
        try
        {
            var detail = await _service.QueryApplicationAsync(Range, id);
            if (version != _version || id != _selectedId || !_active) return;
            if (detail.Application is not { } app) { _selectedId = null; HistoryDetail.Visibility = Visibility.Collapsed; RankingList.SelectedItem = null; return; }
            var m = app.Metrics;
            MetricStatistics? Get(StatisticMetric key) => m.GetValueOrDefault(key);
            var cpu = Get(StatisticMetric.Cpu); var gpu = Get(StatisticMetric.Gpu); var memory = Get(StatisticMetric.Memory); var vram = Get(StatisticMetric.DedicatedGpuMemory);
            DetailName.Text = app.Software.DisplayName; DetailState.Text = (app.IsRunning ? "运行中" : "已退出 · 历史记录") + " · " + SoftwareRow.CategoryName(app.Software.Category);
            DetailObservation.Text = $"已观测运行 {StatisticsDisplay.Duration(app.ObservedSeconds)}\n最后观测 {app.LastSeenUtc.LocalDateTime:MM-dd HH:mm:ss}";
            DetailCpu.Text = Display.Percent(cpu?.Average);
            DetailCpuHint.Text = $"累计耗时 {StatisticsDisplay.CpuTime(cpu?.CpuTicks)}\n采样峰值 {Display.Percent(cpu?.Peak)}{StatisticsDisplay.PeakTime(cpu)}";
            DetailMetrics.Text = $"下载  {Display.Bytes(Get(StatisticMetric.Download)?.Bytes)}\n上传  {Display.Bytes(Get(StatisticMetric.Upload)?.Bytes)}\n磁盘读取  {Display.Bytes(Get(StatisticMetric.DiskRead)?.Bytes)}\n磁盘写入  {Display.Bytes(Get(StatisticMetric.DiskWrite)?.Bytes)}\nGPU 平均  {Display.Percent(gpu?.Average)}\nGPU 峰值  {Display.Percent(gpu?.Peak)}{StatisticsDisplay.PeakTime(gpu)}\n内存峰值  {Display.Bytes(memory?.Peak)}{StatisticsDisplay.PeakTime(memory)}\n专用显存峰值  {Display.Bytes(vram?.Peak)}\n共享显存峰值  {Display.Bytes(Get(StatisticMetric.SharedGpuMemory)?.Peak)}";
            DetailCoverage.Text = string.Join("\n", new[] { ("CPU", StatisticMetric.Cpu), ("GPU", StatisticMetric.Gpu), ("内存", StatisticMetric.Memory), ("专用显存", StatisticMetric.DedicatedGpuMemory), ("下载", StatisticMetric.Download), ("上传", StatisticMetric.Upload), ("磁盘读取", StatisticMetric.DiskRead), ("磁盘写入", StatisticMetric.DiskWrite) }.Select(p => $"{p.Item1}：{StatisticsDisplay.Coverage(Get(p.Item2))}"));
            DetailPath.Text = app.Software.FilePath ?? "路径不可访问"; DetailIdentity.Text = $"{app.Software.ProcessName} · {app.Software.Publisher ?? "发布者未知"}\n{app.Software.ClassificationReason}";
            DetailChart.SetSeries(detail.Trend, snapshot.StartUtc > snapshot.EndUtc.AddDays(-7) ? snapshot.StartUtc : snapshot.EndUtc.AddDays(-7), snapshot.EndUtc, StatisticMetric.Cpu);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DetailState.Text = "详情读取失败：" + ex.Message; }
    }
    private void CloseDetail_Click(object sender, RoutedEventArgs e) { _selectedId = null; HistoryDetail.Visibility = Visibility.Collapsed; RankingList.SelectedItem = null; }
    private void Enhanced_Click(object sender, RoutedEventArgs e) => EnhancedRequested?.Invoke();
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_resetting || ConfirmReset is null) return;
        var range = Range;
        if (!await ConfirmReset(range)) return;
        _resetting = true; ResetButton.IsEnabled = false; _version++;
        try
        {
            await _service.ResetAsync(range);
            _selectedId = null; HistoryDetail.Visibility = Visibility.Collapsed; _nextReorder = default;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Notice.Title = "重置失败"; Notice.Message = ex.Message; Notice.Severity = InfoBarSeverity.Error; Notice.IsOpen = true; }
        finally { _resetting = false; ResetButton.IsEnabled = true; Refresh(); }
    }
}
