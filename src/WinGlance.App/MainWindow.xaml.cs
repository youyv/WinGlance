using System.Collections.ObjectModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinGlance.App.Services;
using WinGlance.App.Controls;
using WinGlance.App.ViewModels;
using WinGlance.Core;
using WinGlance.Statistics;
using Windows.ApplicationModel.DataTransfer;

namespace WinGlance.App;

public sealed partial class MainWindow : Window
{
    private readonly CollectorClient _client = new();
    private readonly ObservableCollection<SoftwareRow> _rows = [];
    private readonly HistoryBuffer _history = new();
    private readonly Queue<(DateTimeOffset Timestamp, double? Cpu, double? Gpu)> _systemHistory = [];
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly StatisticsService _statistics = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance", "statistics.db"));
    private StatisticsView? _statisticsView;
    private bool _statisticsSelected;
    private volatile bool _exitStarted;
    private readonly TrayIcon _tray;
    private MonitorSnapshot? _latest;
    private IReadOnlyList<SoftwareSample> _software = [];
    private OverlayWindow? _overlay;
    private string? _selectedKey;
    private string _filter = "all", _sort = "cpu";
    private bool _descending = true, _paused, _ready, _exiting, _menuOpen, _hidden;
    private bool _dialogOpen;
    private bool _elevated;
    private DateTimeOffset _lastStatusUpdate;
    private DateTimeOffset _nextReorder;
    private string? _capabilityTooltip;
    private readonly Brush _sortAccent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 183, 164));
    private readonly Brush _sortFill = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 56, 183, 164));
    private readonly Brush _clearBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinGlance";
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(1400, work.Width - 32), height = Math.Min(900, work.Height - 32);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinGlance.ico"));
        try
        {
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
                Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException) { }
        ApplyTheme();
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        HideSystemToggle.IsOn = _settings.HideSystem;
        ProcessList.ItemsSource = _rows;
        ProcessList.ContainerContentChanging += (_, args) => ListViewMotion.Reset(args.ItemContainer);
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this))
        {
            ShowRequested = ShowMain,
            OverlayRequested = ToggleOverlay,
            ExitRequested = ExitApp
        };
        AppWindow.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            if (!_exiting && _settings.CloseToTray && _tray.IsAvailable)
            {
                _hidden = true; _statisticsView?.SetActive(false); AppWindow.Hide();
            }
            else ExitApp();
        };
        AppWindow.Changed += (_, args) =>
        {
            if (!_hidden && AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized && _tray.IsAvailable)
            { _hidden = true; _statisticsView?.SetActive(false); AppWindow.Hide(); }
        };
        Closed += (_, _) => { _client.Dispose(); _tray.Dispose(); _overlay?.Close(); _statisticsView?.SetActive(false); };
        _client.SnapshotReceived += snapshot =>
        {
            // Accept immutable source increments before dispatching UI work. Pausing or hiding
            // either page must never stop accounting.
            if (_exitStarted) return;
            _statistics.Accept(snapshot);
            DispatcherQueue.TryEnqueue(() => { if (!_exitStarted) Receive(snapshot); });
        };
        _client.StatusChanged += status => DispatcherQueue.TryEnqueue(() =>
        {
            StatusLabel.Text = status; LiveLabel.Text = "等待采集"; EmptyLabel.Text = status;
        });
        _ready = true;
        UpdateSortHeaders();
        ToolTipService.SetToolTip(NetworkValue, "选定网卡统计；与应用流量合计可能不同。");
        _ = _client.StartAsync();
        if (_settings.ShowOverlay) ToggleOverlay();
    }

    private void Receive(MonitorSnapshot snapshot)
    {
        _latest = snapshot;
        _elevated = snapshot.IsElevated;
        _software = SoftwareAggregator.Aggregate(snapshot.Processes);
        _history.Add(snapshot.Timestamp, _software);
        _systemHistory.Enqueue((snapshot.Timestamp, snapshot.System.CpuPercent, snapshot.System.GpuPercent));
        while (_systemHistory.Count > 0 && (_systemHistory.Peek().Timestamp < snapshot.Timestamp.AddSeconds(-60) || _systemHistory.Count > 600)) _systemHistory.Dequeue();
        _overlay?.Update(snapshot.System);
        if (snapshot.Timestamp - _lastStatusUpdate > TimeSpan.FromSeconds(3))
        {
            _lastStatusUpdate = snapshot.Timestamp;
            _tray.Update($"WinGlance\nCPU {Display.Percent(snapshot.System.CpuPercent)} · GPU {Display.Percent(snapshot.System.GpuPercent)}\n↓ {Display.Rate(snapshot.System.DownloadBytesPerSecond)} ↑ {Display.Rate(snapshot.System.UploadBytesPerSecond)}");
        }
        if (_hidden || _paused) return;
        Render();
    }

    private void Render()
    {
        if (_statisticsSelected || _hidden || _exitStarted) return;
        if (_latest is not { } snapshot) return;
        var system = snapshot.System;
        CpuValue.Text = Display.Percent(system.CpuPercent);
        GpuValue.Text = Display.Percent(system.GpuPercent);
        CpuChart.SetPoints(_systemHistory.Select(p => (p.Timestamp, p.Cpu)), snapshot.Timestamp);
        GpuChart.SetPoints(_systemHistory.Select(p => (p.Timestamp, p.Gpu)), snapshot.Timestamp);
        MemoryValue.Text = Display.Bytes(system.TotalMemoryBytes - system.AvailableMemoryBytes);
        MemoryHint.Text = $"共 {Display.Bytes(system.TotalMemoryBytes)} · 已使用";
        MemoryBar.Value = system.TotalMemoryBytes > 0 ? 100.0 * (system.TotalMemoryBytes - system.AvailableMemoryBytes) / system.TotalMemoryBytes : 0;
        NetworkValue.Text = "↓ " + Display.Rate(system.DownloadBytesPerSecond);
        UploadValue.Text = "↑ " + Display.Rate(system.UploadBytesPerSecond);
        NetworkHint.Text = system.NetworkInterface;
        DiskValue.Text = Display.Rate(system.DiskReadBytesPerSecond);
        DiskWriteValue.Text = "写 " + Display.Rate(system.DiskWriteBytesPerSecond);
        GpuHint.Text = system.GpuPercent is null ? "计数器不可用 / 预热中" : "最忙引擎 · 所有适配器";
        LiveLabel.Text = "实时 · 1 秒";
        var unavailable = snapshot.Capabilities.Where(p => p.Value.State != MetricState.Ready).ToArray();
        StatusLabel.Text = $"{snapshot.Timestamp.LocalDateTime:HH:mm:ss} 更新 · {snapshot.Processes.Count} 个进程 · " +
            (unavailable.Length == 0 ? "采集正常" : string.Join(" · ", unavailable.Select(p => $"{CapabilityName(p.Key)}：{p.Value.Message}")));
        var capabilityTooltip = string.Join("\n", snapshot.Capabilities.Select(p => $"{CapabilityName(p.Key)}：{p.Value.Message}"));
        if (_capabilityTooltip != capabilityTooltip)
        {
            _capabilityTooltip = capabilityTooltip;
            ToolTipService.SetToolTip(StatusLabel, capabilityTooltip);
        }
        if (!_menuOpen) RefreshRows(false);
        UpdateDetail();
    }

    private void RefreshRows(bool forceReorder = true)
    {
        if (!_ready) return;
        string query = SearchBox.Text.Trim();
        var hidden = _software.Count(s => s.Software.Category == SoftwareCategory.WindowsSystem);
        IEnumerable<SoftwareSample> filtered = _software;
        if (HideSystemToggle.IsOn) filtered = filtered.Where(s => s.Software.Category != SoftwareCategory.WindowsSystem);
        if (query.Length > 0) filtered = filtered.Where(s => s.Software.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (s.Software.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || s.Processes.Any(p =>
                p.Software.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Identity.Pid.ToString().Contains(query)));
        if (_filter == "network") filtered = filtered.Where(s => (s.DownloadBytesPerSecond ?? 0) + (s.UploadBytesPerSecond ?? 0) >= 1);
        if (_filter == "gpu") filtered = filtered.Where(s => s.GpuPercent > 0);
        var positions = _rows.Where(r => !r.IsChild).Select((r, index) => (r.Key, index))
            .ToDictionary(p => p.Key, p => p.index, StringComparer.OrdinalIgnoreCase);
        static double? Rounded(double? value) => value is { } number ? Math.Round(number, 1) : null;
        Func<SoftwareSample, double?> selector = _sort switch
        {
            "gpu" => s => Rounded(s.GpuPercent), "memory" => s => s.PrivateWorkingSetBytes, "down" => s => s.DownloadBytesPerSecond,
            "up" => s => s.UploadBytesPerSecond, "disk" => s => s.DiskReadBytesPerSecond + s.DiskWriteBytesPerSecond, _ => s => Rounded(s.CpuPercent)
        };
        var sorted = _sort == "name" ? (_descending ? filtered.OrderByDescending(s => s.Software.DisplayName) : filtered.OrderBy(s => s.Software.DisplayName)) :
            _descending ? filtered.OrderBy(s => selector(s) is null).ThenByDescending(selector).ThenBy(s => positions.GetValueOrDefault(s.Key, int.MaxValue)).ThenBy(s => s.Key) :
                filtered.OrderBy(s => selector(s) is null).ThenBy(selector).ThenBy(s => positions.GetValueOrDefault(s.Key, int.MaxValue)).ThenBy(s => s.Key);
        bool reorder = forceReorder || DateTimeOffset.UtcNow >= _nextReorder;
        var items = reorder ? sorted.ToArray() : filtered.OrderBy(s => positions.GetValueOrDefault(s.Key, int.MaxValue)).ToArray();
        if (reorder) _nextReorder = DateTimeOffset.UtcNow.AddSeconds(3);
        var desired = new List<(string Key, SoftwareSample Software, ProcessSample? Process)>();
        foreach (var software in items)
        {
            desired.Add((software.Key, software, null));
            if (_expanded.Contains(software.Key)) foreach (var process in software.Processes.OrderBy(p => p.Identity.Pid))
                desired.Add(($"{software.Key}|{process.Identity.Pid}:{process.Identity.StartTimeUtcTicks}", software, process));
        }
        bool orderChanged = !_rows.Select(r => r.Key).SequenceEqual(desired.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        var selectedRow = ProcessList.SelectedItem as SoftwareRow;
        var previousPositions = orderChanged ? ListViewMotion.Capture(ProcessList) : null;
        var keep = desired.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = _rows.Count - 1; i >= 0; i--) if (!keep.Contains(_rows[i].Key)) _rows.RemoveAt(i);
        var byKey = _rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < desired.Count; i++)
        {
            var value = desired[i];
            if (byKey.TryGetValue(value.Key, out var row))
            {
                int old = i < _rows.Count && ReferenceEquals(_rows[i], row) ? i : _rows.IndexOf(row);
                if (old != i)
                {
                    // WinUI's bindable vector adapter turns ObservableCollection.Move into Reset.
                    // Explicit incremental notifications preserve unrelated realized containers.
                    _rows.RemoveAt(old);
                    _rows.Insert(i, row);
                }
            }
            else { row = new SoftwareRow(value.Key, value.Software, value.Process); _rows.Insert(i, row); }
            row.Update(value.Software, value.Process, _expanded.Contains(value.Software.Key));
        }
        if (orderChanged)
        {
            if (selectedRow is not null && _rows.Contains(selectedRow)) ProcessList.SelectedItem = selectedRow;
            ListViewMotion.Animate(ProcessList, previousPositions);
        }
        var active = _software.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _expanded.RemoveWhere(key => !active.Contains(key));
        CountLabel.Text = $"{items.Length} 个应用" + (HideSystemToggle.IsOn ? $" · 已隐藏 {hidden} 项" : "");
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Text = _latest is null ? "等待第一份采集数据…" : "没有匹配的应用";
        if (_filter == "network" && _latest?.Capabilities.GetValueOrDefault("network") is { State: not MetricState.Ready } network)
            EmptyLabel.Text = network.State == MetricState.PermissionRequired ? "启用增强采集后，可以筛选正在联网的应用。" : network.Message;
        if (_filter == "gpu" && _latest?.Capabilities.GetValueOrDefault("gpu") is { State: not MetricState.Ready } gpu)
            EmptyLabel.Text = gpu.Message;
    }

    private void UpdateDetail()
    {
        if (_selectedKey is null || DetailPanel.Visibility != Visibility.Visible) return;
        var item = _software.FirstOrDefault(s => s.Key == _selectedKey);
        if (item is null) { DetailSubtitle.Text = "应用已退出"; return; }
        DetailName.Text = item.Software.DisplayName;
        DetailSubtitle.Text = $"{item.Processes.Count} 个进程 · {item.Software.Publisher ?? SoftwareRow.CategoryName(item.Software.Category)}";
        DetailIcon.Source = _rows.FirstOrDefault(r => r.Key == item.Key)?.Icon;
        DetailCpu.Text = Display.Percent(item.CpuPercent); DetailGpu.Text = Display.Percent(item.GpuPercent);
        DetailVram.Text = $"专用 {Display.Bytes(item.DedicatedGpuBytes)}  ·  共享 {Display.Bytes(item.SharedGpuBytes)}";
        DetailNetwork.Text = $"↓ {Display.Rate(item.DownloadBytesPerSecond)}    ↑ {Display.Rate(item.UploadBytesPerSecond)}";
        DetailPath.Text = item.Software.FilePath ?? "路径不可访问";
        DetailClassification.Text = $"{SoftwareRow.CategoryName(item.Software.Category)} · {item.Software.ClassificationReason}";
        DetailProcesses.Text = string.Join("\n", item.Processes.Select(p => $"{p.Software.ProcessName} · PID {p.Identity.Pid}"));
        var history = _history.Get(item.Key);
        var end = _latest?.Timestamp ?? DateTimeOffset.Now;
        DetailCpuChart.SetPoints(history.Select(p => (p.Timestamp, p.CpuPercent)), end);
        DetailGpuChart.SetPoints(history.Select(p => (p.Timestamp, p.GpuPercent)), end);
        DetailDownloadChart.SetPoints(history.Select(p => (p.Timestamp, p.DownloadBytesPerSecond)), end);
        DetailUploadChart.SetPoints(history.Select(p => (p.Timestamp, p.UploadBytesPerSecond)), end);
    }

    private void Process_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SoftwareRow row) return;
        _selectedKey = row.Sample.Key; DetailPanel.Visibility = Visibility.Visible; UpdateDetail();
    }
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }) { if (!_expanded.Add(key)) _expanded.Remove(key); RefreshRows(); }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RefreshRows(); }
    private void HideSystem_Toggled(object sender, RoutedEventArgs e) { if (!_ready) return; _settings.HideSystem = HideSystemToggle.IsOn; _settings.Save(); _statisticsView?.SetHideSystem(_settings.HideSystem); if (!_statisticsSelected) RefreshRows(); }
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _filter = (sender as FrameworkElement)?.Tag as string ?? "all";
        AllFilter.IsChecked = _filter == "all"; NetworkFilter.IsChecked = _filter == "network"; GpuFilter.IsChecked = _filter == "gpu";
        RefreshRows();
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag as string ?? "cpu";
        _descending = key == _sort ? !_descending : key != "name"; _sort = key;
        UpdateSortHeaders();
        RefreshRows();
    }
    private void UpdateSortHeaders()
    {
        (Button Button, string Label, string Key)[] headers =
        [ (NameHeader, "应用", "name"), (CpuHeader, "CPU", "cpu"), (GpuHeader, "GPU", "gpu"),
          (MemoryHeader, "内存", "memory"), (DownloadHeader, "下载", "down"), (UploadHeader, "上传", "up"), (DiskHeader, "磁盘 I/O", "disk") ];
        foreach (var (button, label, key) in headers)
        {
            bool active = key == _sort;
            button.Content = label + (active ? (_descending ? " ↓" : " ↑") : "");
            button.Padding = new Thickness(8, 4, 8, 4);
            button.Background = active ? _sortFill : _clearBrush;
            button.BorderBrush = active ? _sortAccent : _clearBrush;
            button.BorderThickness = new Thickness(1);
            button.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            var help = active ? $"当前按{label}{(_descending ? "降序" : "升序")}排列；点击切换为{(_descending ? "升序" : "降序")}。" : $"点击按{label}{(key == "name" ? "升序" : "降序")}排列。";
            ToolTipService.SetToolTip(button, help);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, help);
            if (active) SortHint.Text = $"当前：{label} {(_descending ? "↓ 降序" : "↑ 升序")} · 点击列标题切换";
        }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { _paused = !_paused; PauseButton.Content = _paused ? "恢复显示" : "暂停显示"; LiveLabel.Text = _paused ? "显示已暂停" : "实时 · 1 秒"; if (!_paused) Render(); }
    private void Elevate_Click(object sender, RoutedEventArgs e) { _ = _client.StartAsync(true); }
    private void Reconnect_Click(object sender, RoutedEventArgs e) { _ = _client.StartAsync(_elevated); }
    private void CloseDetail_Click(object sender, RoutedEventArgs e) { DetailPanel.Visibility = Visibility.Collapsed; _selectedKey = null; }
    private void OpenLocation_Click(object sender, RoutedEventArgs e) => RunAction(() => ProcessActions.OpenLocation(_software.FirstOrDefault(s => s.Key == _selectedKey)?.Software.FilePath));
    private void Overlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SoftwareRow row } element) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        var path = row.Process?.Software.FilePath ?? row.Sample.Software.FilePath;
        void Item(string title, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
            item.Click += (_, _) => RunAction(action); menu.Items.Add(item);
        }
        Item("打开文件所在位置", () => ProcessActions.OpenLocation(path), path is not null);
        Item("复制路径", () => { var data = new DataPackage(); data.SetText(path!); Clipboard.SetContent(data); }, path is not null);
        Item("文件属性", () => ProcessActions.Properties(path), path is not null);
        menu.Items.Add(new MenuFlyoutSeparator());
        var terminate = new MenuFlyoutItem { Text = row.IsChild ? "结束此进程…" : $"结束应用（{row.Sample.Processes.Count} 个进程）…", IsEnabled = row.Sample.Software.Category != SoftwareCategory.WindowsSystem };
        terminate.Click += async (_, _) => await TerminateAsync(row);
        menu.Items.Add(terminate);
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
    }
    private async Task TerminateAsync(SoftwareRow row)
    {
        if (_dialogOpen) return;
        var targets = row.Process is { } process ? new[] { process } : row.Sample.Processes.ToArray();
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"结束 {row.Name}？", Content = $"将结束 {targets.Length} 个进程。未保存的内容可能丢失。\nPID：{string.Join(", ", targets.Select(p => p.Identity.Pid))}", PrimaryButtonText = "结束任务", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var failures = new List<string>();
            foreach (var target in targets)
            {
                try { ProcessActions.Terminate(target); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { failures.Add($"PID {target.Identity.Pid}：{ex.Message}"); }
            }
            if (failures.Count > 0) StatusLabel.Text = string.Join(" · ", failures);
        }
        finally { _dialogOpen = false; }
    }
    private async void RunAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            StatusLabel.Text = ex.Message;
            if (_dialogOpen) return;
            _dialogOpen = true;
            try { await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "操作未完成", Content = ex.Message, CloseButtonText = "知道了" }.ShowAsync(); }
            finally { _dialogOpen = false; }
        }
    }
    internal void ActivateFromAnotherInstance() { if (!_exitStarted) ShowMain(); }
    private void ShowMain() { _hidden = false; AppWindow.Show(); if (AppWindow.Presenter is OverlappedPresenter p) p.Restore(); Activate(); _statisticsView?.SetActive(_statisticsSelected); Render(); }
    private void ToggleOverlay()
    {
        if (_overlay is not null) { _overlay.Close(); return; }
        _overlay = new OverlayWindow(_settings.Theme);
        _overlay.Closed += (_, _) => { _overlay = null; _settings.ShowOverlay = false; _settings.Save(); };
        _overlay.Activate(); if (_latest is not null) _overlay.Update(_latest.System);
        _settings.ShowOverlay = true; _settings.Save();
    }
    private async void ExitApp()
    {
        if (_exitStarted) return;
        _exitStarted = true;
        _client.Dispose(); _statisticsView?.SetActive(false);
        StatusLabel.Text = "正在保存统计…";
        try { await _statistics.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance", "statistics-error.log"), $"{DateTimeOffset.Now:O} exit: {ex}\n"); } catch { }
        }
        _exiting = true; _overlay?.Close(); _tray.Dispose(); Close(); Application.Current.Exit();
    }

    private void Overview_Click(object sender, RoutedEventArgs e) => Navigate(false);
    private void Statistics_Click(object sender, RoutedEventArgs e) => Navigate(true);
    private void Navigate(bool statistics)
    {
        if (_statisticsSelected == statistics || _exitStarted) return;
        if (statistics && _statisticsView is null)
        {
            _statisticsView = new StatisticsView(_statistics, _settings.HideSystem) { ConfirmReset = ConfirmStatisticsResetAsync };
            _statisticsView.HideSystemChanged += hidden => { HideSystemToggle.IsOn = hidden; };
            _statisticsView.EnhancedRequested += () => { _ = _client.StartAsync(true); };
            StatisticsHost.Content = _statisticsView;
        }
        _statisticsSelected = statistics;
        DetailPanel.Visibility = Visibility.Collapsed; _selectedKey = null;
        OverviewContent.Visibility = statistics ? Visibility.Collapsed : Visibility.Visible;
        StatisticsHost.Visibility = statistics ? Visibility.Visible : Visibility.Collapsed;
        OverviewMarker.Visibility = statistics ? Visibility.Collapsed : Visibility.Visible;
        StatisticsMarker.Visibility = statistics ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(OverviewButton, statistics ? "切换到资源概览" : "当前页面");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(StatisticsButton, statistics ? "当前页面" : "切换到运行统计");
        _statisticsView?.SetActive(statistics && !_hidden);
        if (!statistics) Render();
        var target = statistics ? (UIElement)StatisticsHost : OverviewContent;
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(target);
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, .5f); animation.InsertKeyFrame(1, 1); animation.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("Opacity", animation);
        }
    }
    private async Task<bool> ConfirmStatisticsResetAsync(StatisticsRange range)
    {
        if (_dialogOpen) return false;
        _dialogOpen = true;
        try
        {
            string name = range switch { StatisticsRange.Today => "今日", StatisticsRange.LastSevenDays => "近 7 天", _ => "本次运行" };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"重置{name}的统计？",
                Content = $"将清除{name}截至重置时的整机与全部应用统计，包括已隐藏的 Windows 系统进程。\n\n其他时间范围中重叠的数据也会改变，操作无法撤销。主题、开机启动和其他偏好保持不变。重置完成后从新的采样边界继续累计。",
                PrimaryButtonText = "重置统计", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _dialogOpen = false; }
    }
    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _overlay?.ApplyTheme(_settings.Theme);
        UpdateTitleBar();
    }
    private void UpdateTitleBar()
    {
        int dark = Root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, ref dark, sizeof(int));
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var theme = new ComboBox { Header = "外观", HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(theme, "ThemePicker");
            theme.Items.Add("跟随系统"); theme.Items.Add("浅色"); theme.Items.Add("深色");
            theme.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            var startup = new ToggleSwitch { Header = "开机启动", OnContent = "已添加启动项", OffContent = "未添加启动项" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(startup, "StartupToggle");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(startup, "开机启动");
            var startupNotice = new InfoBar { Title = "开机启动", IsClosable = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(startupNotice, "StartupNotice");
            try
            {
                var startupStatus = StartupRegistration.GetStatus();
                startup.IsOn = startupStatus.HasRegistration;
                if (startupStatus.State is StartupRegistrationState.DifferentCommand or StartupRegistrationState.InvalidValue)
                {
                    startupNotice.Severity = InfoBarSeverity.Warning;
                    startupNotice.Message = "已有启动项需要更新。保存后将使用当前应用位置；关闭开关可移除它。";
                    startupNotice.IsOpen = true;
                }
            }
            catch (Exception ex) when (IsStartupSettingsError(ex))
            {
                startup.IsEnabled = false;
                startup.OffContent = "状态不可用";
                startupNotice.Severity = InfoBarSeverity.Error;
                startupNotice.Message = $"无法读取开机启动状态：{ex.Message}";
                startupNotice.IsOpen = true;
            }
            var startupPanel = new StackPanel { Spacing = 8 };
            startupPanel.Children.Add(startup);
            startupPanel.Children.Add(new TextBlock
            {
                Text = "登录 Windows 后自动打开 WinGlance。\n如果已在 Windows 的“启动应用”中禁用，请在那里重新启用。",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Style = (Style)Application.Current.Resources["Muted"]
            });
            startupPanel.Children.Add(startupNotice);
            var tray = new ToggleSwitch { Header = "关闭主窗口时", OnContent = "保留在托盘继续监控", OffContent = "退出应用", IsOn = _settings.CloseToTray };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tray, "CloseToTrayToggle");
            var panel = new StackPanel { Spacing = 20 }; panel.Children.Add(theme); panel.Children.Add(startupPanel); panel.Children.Add(tray);
            panel.Children.Add(new TextBlock { Text = "刷新频率：1 秒\n趋势保留：60 秒\n进程内存：私有工作集\n增强采集仅提升采集器权限", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "设置", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { if (startup.IsEnabled) StartupRegistration.SetEnabled(startup.IsOn); }
                catch (Exception ex) when (IsStartupSettingsError(ex))
                {
                    args.Cancel = true;
                    startupNotice.Title = "开机启动";
                    startupNotice.Severity = InfoBarSeverity.Error;
                    startupNotice.Message = $"无法保存开机启动设置：{ex.Message}";
                    startupNotice.IsOpen = true;
                    return;
                }
                var previousTheme = _settings.Theme;
                var previousCloseToTray = _settings.CloseToTray;
                _settings.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
                _settings.CloseToTray = tray.IsOn;
                if (!_settings.TrySave(out var error))
                {
                    _settings.Theme = previousTheme;
                    _settings.CloseToTray = previousCloseToTray;
                    args.Cancel = true;
                    startupNotice.Title = "设置未完全保存";
                    startupNotice.Severity = InfoBarSeverity.Error;
                    startupNotice.Message = (startup.IsEnabled ? "开机启动设置已应用；" : "") + $"外观和关闭行为保存失败：{error}";
                    startupNotice.IsOpen = true;
                }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ApplyTheme();
            }
        }
        finally { _dialogOpen = false; }
    }
    private static bool IsStartupSettingsError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException;
    private static string CapabilityName(string key) => key switch { "cpu" => "CPU", "memory" => "内存", "network" => "进程网络", "disk" => "进程磁盘", "gpu" => "GPU", "gpuMemory" => "显存", "systemNetwork" => "网卡", "systemDisk" => "物理磁盘", _ => key };
}
