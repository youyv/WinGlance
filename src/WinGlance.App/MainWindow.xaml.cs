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
    private readonly ConnectionDetailsMonitor _connections = new();
    private IReadOnlyList<ProcessConnection> _connectionRows = [];
    private long _connectionGeneration;
    private readonly ObservableCollection<SoftwareRow> _rows = [];
    private readonly LiveSnapshotStore _live = new();
    private int _uiQueued;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly StatisticsService _statistics = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance", "statistics.db"));
    private StatisticsView? _statisticsView;
    private bool _statisticsSelected;
    private volatile bool _exitStarted;
    private readonly TrayIcon _tray;
    private readonly SessionEndCoordinator _sessionEnd;
    private MonitorSnapshot? _latest;
    private IReadOnlyList<SoftwareSample> _software = [];
    private OverlayWindow? _overlay;
    private string? _selectedKey;
    private string _filter = "all", _sort = "cpu";
    private bool _descending = true, _paused, _ready, _exiting, _menuOpen, _hidden;
    private bool _dialogOpen;
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
        _sessionEnd = new SessionEndCoordinator(
            () => { _exitStarted = true; _connections.Dispose(); _statisticsView?.SetActive(false); _client.Dispose(); _overlay?.PrepareForExit(); },
            () => _statistics.FlushAsync(), FinishPersistenceAsync, LogSaveError);
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this))
        {
            ShowRequested = ShowMain,
            OverlayRequested = ToggleOverlay,
            OverlayInteractionRequested = RestoreOverlayInteraction,
            ExitRequested = ExitApp,
            SessionQuery = _sessionEnd.Query,
            DisplayChanged = () => _overlay?.ScheduleRecovery(),
            SessionEnd = confirmed => { if (confirmed) _exiting = true; _sessionEnd.End(confirmed, TimeSpan.FromSeconds(2)); }
        };
        AppWindow.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            if (!_exiting && _settings.CloseToTray && _tray.IsAvailable)
            {
                _hidden = true; _connections.Deactivate(); _statisticsView?.SetActive(false); AppWindow.Hide();
            }
            else ExitApp();
        };
        AppWindow.Changed += (_, args) =>
        {
            if (!_hidden && AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized && _tray.IsAvailable)
            { _hidden = true; _connections.Deactivate(); _statisticsView?.SetActive(false); AppWindow.Hide(); }
        };
        Closed += (_, _) => { _connections.Dispose(); _client.Dispose(); _tray.Dispose(); _overlay?.Close(); _statisticsView?.SetActive(false); };
        _connections.Updated += update => DispatcherQueue.TryEnqueue(() =>
        {
            if (_exitStarted || _hidden || _paused || _statisticsSelected || !_connections.IsCurrent(update)) return;
            ConnectionHint.Text = update.Result.Description;
            if (!_connectionRows.SequenceEqual(update.Result.Connections))
            {
                _connectionRows = update.Result.Connections;
                ConnectionsList.ItemsSource = _connectionRows;
            }
            ConnectionEmpty.Visibility = _connectionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConnectionEmpty.Text = update.Result.UnverifiedProcesses > 0 || update.Result.UnavailableTables.Count > 0 ? "部分连接无法读取，详见上方提示" : "当前没有 TCP 连接或 UDP 绑定";
        });
        _client.SnapshotReceived += snapshot =>
        {
            // Accept immutable source increments before dispatching UI work. Pausing or hiding
            // either page must never stop accounting.
            if (_exitStarted) return;
            _statistics.Accept(snapshot);
            // The reader maintains all 60-second histories even while UI frames are coalesced or hidden.
            _live.Accept(snapshot);
            if (Interlocked.Exchange(ref _uiQueued, 1) == 0 && !DispatcherQueue.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref _uiQueued, 0);
                    if (!_exitStarted && _live.Latest is { } frame) Receive(frame);
                })) Interlocked.Exchange(ref _uiQueued, 0);
        };
        _client.ConnectionChanged += () => DispatcherQueue.TryEnqueue(UpdateCollectionState);
        _ready = true;
        UpdateSortHeaders();
        ToolTipService.SetToolTip(NetworkValue, "选定网卡统计；与应用流量合计可能不同。");
        _ = _client.StartAsync();
        if (_settings.ShowOverlay) ToggleOverlay();
    }

    private void Receive(LiveFrame frame)
    {
        var snapshot = frame.Snapshot;
        if (_latest is { } previous && snapshot.Timestamp < previous.Timestamp) { _nextReorder = default; _lastStatusUpdate = default; }
        _latest = snapshot;
        _software = frame.Software;
        _overlay?.Update(OverviewSelection.Apply(snapshot.System, _settings.NetworkInterfaceId, _settings.PhysicalDiskId));
        if (snapshot.Timestamp - _lastStatusUpdate > TimeSpan.FromSeconds(3))
        {
            _lastStatusUpdate = snapshot.Timestamp;
            _tray.Update($"WinGlance\nCPU {Display.Percent(snapshot.System.CpuPercent)} · GPU {Display.Percent(snapshot.System.GpuPercent)}\n↓ {Display.Rate(snapshot.System.DownloadBytesPerSecond)} ↑ {Display.Rate(snapshot.System.UploadBytesPerSecond)}");
        }
        if (_hidden) return;
        if (_paused) { UpdateCollectionState(); return; }
        Render();
    }

    private void Render()
    {
        UpdateCollectionState();
        if (_statisticsSelected || _hidden || _exitStarted) return;
        if (_latest is not { } snapshot) return;
        var system = OverviewSelection.Apply(snapshot.System, _settings.NetworkInterfaceId, _settings.PhysicalDiskId);
        CpuValue.Text = Display.Percent(system.CpuPercent);
        GpuValue.Text = Display.Percent(system.GpuPercent);
        var systemHistory = _live.SystemHistory();
        CpuChart.SetPoints(systemHistory.Select(p => (p.Timestamp, p.Cpu)), snapshot.Timestamp);
        GpuChart.SetPoints(systemHistory.Select(p => (p.Timestamp, p.Gpu)), snapshot.Timestamp);
        MemoryValue.Text = Display.Bytes(system.TotalMemoryBytes - system.AvailableMemoryBytes);
        MemoryHint.Text = $"共 {Display.Bytes(system.TotalMemoryBytes)} · 已使用";
        MemoryBar.Value = system.TotalMemoryBytes > 0 ? 100.0 * (system.TotalMemoryBytes - system.AvailableMemoryBytes) / system.TotalMemoryBytes : 0;
        NetworkValue.Text = "↓ " + Display.Rate(system.DownloadBytesPerSecond);
        UploadValue.Text = "↑ " + Display.Rate(system.UploadBytesPerSecond);
        NetworkHint.Text = system.NetworkInterface;
        DiskValue.Text = Display.Rate(system.DiskReadBytesPerSecond);
        DiskWriteValue.Text = "写 " + Display.Rate(system.DiskWriteBytesPerSecond);
        DiskHint.Text = string.IsNullOrEmpty(_settings.PhysicalDiskId) ? "全部物理磁盘" :
            snapshot.System.PhysicalDisks.FirstOrDefault(d => d.Id == _settings.PhysicalDiskId)?.Name ?? "所选磁盘不可用";
        GpuHint.Text = system.GpuPercent is null ? "计数器不可用 / 预热中" : "最忙引擎 · 所有适配器";
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
        if (item is null) { DetailSubtitle.Text = "应用已退出"; UpdateConnectionDetails(); return; }
        DetailName.Text = item.Software.DisplayName;
        DetailSubtitle.Text = $"{item.Processes.Count} 个进程 · {item.Software.Publisher ?? SoftwareRow.CategoryName(item.Software.Category)}";
        DetailIcon.Source = _rows.FirstOrDefault(r => r.Key == item.Key)?.Icon;
        DetailCpu.Text = Display.Percent(item.CpuPercent); DetailGpu.Text = Display.Percent(item.GpuPercent);
        DetailVram.Text = $"专用 {Display.Bytes(item.DedicatedGpuBytes)}  ·  共享 {Display.Bytes(item.SharedGpuBytes)}";
        DetailNetwork.Text = $"↓ {Display.Rate(item.DownloadBytesPerSecond)}    ↑ {Display.Rate(item.UploadBytesPerSecond)}";
        DetailPath.Text = item.Software.FilePath ?? "路径不可访问";
        DetailClassification.Text = $"{SoftwareRow.CategoryName(item.Software.Category)} · {item.Software.ClassificationReason}";
        DetailProcesses.Text = string.Join("\n", item.Processes.Select(p => $"{p.Software.ProcessName} · PID {p.Identity.Pid}"));
        var history = _live.History.Get(item.Key);
        var end = _latest?.Timestamp ?? DateTimeOffset.Now;
        DetailCpuChart.SetPoints(history.Select(p => (p.Timestamp, p.CpuPercent)), end);
        DetailGpuChart.SetPoints(history.Select(p => (p.Timestamp, p.GpuPercent)), end);
        DetailDownloadChart.SetPoints(history.Select(p => (p.Timestamp, p.DownloadBytesPerSecond)), end);
        DetailUploadChart.SetPoints(history.Select(p => (p.Timestamp, p.UploadBytesPerSecond)), end);
        UpdateConnectionDetails();
    }

    private void UpdateConnectionDetails()
    {
        if (_hidden || _paused || _statisticsSelected || _exitStarted || DetailPanel.Visibility != Visibility.Visible || !_client.Connection.Connected)
        {
            _connections.Deactivate();
            ConnectionHint.Text = "连接详情已暂停";
            return;
        }
        var selected = _software.FirstOrDefault(s => s.Key == _selectedKey);
        if (selected is null)
        {
            _connections.Deactivate(); _connectionRows = []; ConnectionsList.ItemsSource = _connectionRows;
            ConnectionHint.Text = "应用已退出"; ConnectionEmpty.Text = "当前没有可查询的进程"; ConnectionEmpty.Visibility = Visibility.Visible;
            return;
        }
        long generation = _connections.Activate(selected.Key, selected.Processes.Select(p => p.Identity));
        if (generation != _connectionGeneration)
        {
            _connectionGeneration = generation; _connectionRows = []; ConnectionsList.ItemsSource = _connectionRows;
            ConnectionHint.Text = "正在查询连接…"; ConnectionEmpty.Visibility = Visibility.Collapsed;
        }
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
    private void Pause_Click(object sender, RoutedEventArgs e) { _paused = !_paused; PauseButton.Content = _paused ? "恢复显示" : "暂停显示"; UpdateCollectionState(); UpdateConnectionDetails(); if (!_paused) Render(); }
    private void Elevate_Click(object sender, RoutedEventArgs e) { _ = _client.StartAsync(true); }
    private void Reconnect_Click(object sender, RoutedEventArgs e) { _ = _client.StartAsync(false, forceReconnect: true); }
    private void CollectionNotice_CloseButtonClick(InfoBar sender, object args) => _client.DismissNotice();
    private void CloseDetail_Click(object sender, RoutedEventArgs e) { _connections.Deactivate(); DetailPanel.Visibility = Visibility.Collapsed; _selectedKey = null; }
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
    internal void ShowInitial(IEnumerable<string> arguments)
    {
        if (StartupRegistration.ShouldHideOnLaunch(arguments, _tray.IsAvailable))
        {
            _hidden = true;
            AppWindow.Hide();
        }
        else { Activate(); UpdateTitleBar(); }
    }
    private void ShowMain() { _hidden = false; AppWindow.Show(); if (AppWindow.Presenter is OverlappedPresenter p) p.Restore(); Activate(); UpdateTitleBar(); _statisticsView?.SetActive(_statisticsSelected); Render(); }
    private void ToggleOverlay()
    {
        if (_overlay is not null) { _overlay.Close(); return; }
        if (!_tray.IsAvailable) _settings.OverlayClickThrough = false;
        try { _overlay = new OverlayWindow(_settings); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _settings.ShowOverlay = false; _settings.Save();
            ShowOverlayFailure("悬浮条未能打开：" + ex.Message);
            return;
        }
        _overlay.ClickThroughChanged += enabled => { _tray.CanRestoreOverlayInteraction = enabled; if (!_exitStarted) _settings.Save(); };
        _overlay.AppearanceFailed += message =>
        {
            if (_exitStarted) return;
            _settings.Save(); ShowOverlayFailure(message);
        };
        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            _tray.CanRestoreOverlayInteraction = false;
            if (!_exitStarted && !_exiting) { _settings.ShowOverlay = false; _settings.Save(); }
        };
        _overlay.ShowOverlay(out _); if (_latest is not null) _overlay.Update(OverviewSelection.Apply(_latest.System, _settings.NetworkInterfaceId, _settings.PhysicalDiskId));
        _settings.ShowOverlay = true; _settings.Save();
    }
    private void ShowOverlayFailure(string message)
    {
        OverlayNotice.Message = message; OverlayNotice.IsOpen = true;
        if (!_dialogOpen) { Navigate(false); ShowMain(); }
    }
    private void RestoreOverlayInteraction()
    {
        _settings.OverlayClickThrough = false;
        _overlay?.ApplyAppearance(out _);
        _tray.CanRestoreOverlayInteraction = false;
        _settings.Save();
    }
    private async void ExitApp()
    {
        if (_exitStarted) return;
        _statisticsView?.SetActive(false);
        StatusLabel.Text = "正在保存统计…";
        await _sessionEnd.FinishAsync();
        _exiting = true; _overlay?.Close(); _tray.Dispose(); Close(); Application.Current.Exit();
    }
    private static void LogSaveError(string message)
    {
        try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance", "statistics-error.log"), $"{DateTimeOffset.Now:O} exit: {message}\n"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task FinishPersistenceAsync()
    {
        // Serialize settings on the UI thread, then write/drain without blocking it.
        var save = _settings.SaveAsync();
        try { await _statistics.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            // A statistics failure must not let ExitApp terminate an unfinished settings write.
            var error = await save.ConfigureAwait(false);
            if (error is not null) LogSaveError(error);
        }
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
            _statisticsView.ReconnectRequested += () => { _ = _client.StartAsync(false, forceReconnect: true); };
            _statisticsView.CollectionNoticeDismissed += _client.DismissNotice;
            StatisticsHost.Content = _statisticsView;
            UpdateCollectionState();
        }
        _statisticsSelected = statistics;
        _connections.Deactivate();
        DetailPanel.Visibility = Visibility.Collapsed; _selectedKey = null;
        OverviewContent.Visibility = statistics ? Visibility.Collapsed : Visibility.Visible;
        StatisticsHost.Visibility = statistics ? Visibility.Visible : Visibility.Collapsed;
        OverviewMarker.Visibility = statistics ? Visibility.Collapsed : Visibility.Visible;
        StatisticsMarker.Visibility = statistics ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(OverviewButton, statistics ? "切换到资源概览" : "当前页面");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(StatisticsButton, statistics ? "当前页面" : "切换到运行统计");
        _statisticsView?.SetActive(statistics && !_hidden);
        UpdateCollectionState();
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
    private void UpdateCollectionState()
    {
        if (_exitStarted) return;
        var state = _client.Connection;
        if (!state.Connected) { _connections.Deactivate(); ConnectionHint.Text = "连接详情已暂停"; }
        var presentation = CollectionPresentation.Create(state, _latest, _paused, DateTimeOffset.UtcNow);
        EnhancedButton.Content = presentation.Label;
        EnhancedButton.IsEnabled = presentation.CanEnhance;
        ReconnectButton.IsEnabled = presentation.CanReconnect;
        ToolTipService.SetToolTip(EnhancedButton, state.Message);
        LiveLabel.Text = presentation.Live;
        StatusLabel.Text = presentation.Status;
        if (_latest is null) EmptyLabel.Text = state.Message;
        CollectionNotice.Message = presentation.Notice ?? "";
        CollectionNotice.IsOpen = presentation.Notice is not null;
        _statisticsView?.SetCollectionState(presentation);
    }
    private void NetworkDevice_Click(object sender, RoutedEventArgs e) => ShowDeviceMenu((Button)sender, true);
    private void DiskDevice_Click(object sender, RoutedEventArgs e) => ShowDeviceMenu((Button)sender, false);
    private void ShowDeviceMenu(Button target, bool network)
    {
        var menu = new MenuFlyout();
        string? selected = network ? _settings.NetworkInterfaceId : _settings.PhysicalDiskId;
        void Add(string? id, string label)
        {
            var item = new MenuFlyoutItem { Text = (string.Equals(selected, id, StringComparison.Ordinal) ? "✓ " : "    ") + label };
            item.Click += (_, _) =>
            {
                if (network) _settings.NetworkInterfaceId = id; else _settings.PhysicalDiskId = id;
                _settings.Save(); Render();
                if (_latest is not null) _overlay?.Update(OverviewSelection.Apply(_latest.System, _settings.NetworkInterfaceId, _settings.PhysicalDiskId));
            };
            menu.Items.Add(item);
        }
        Add(null, network ? "自动选择活动网卡" : "全部物理磁盘");
        menu.Items.Add(new MenuFlyoutSeparator());
        var source = _latest?.System;
        bool found = selected is null;
        if (network)
        {
            foreach (var device in source?.NetworkInterfaces ?? [])
            {
                Add(device.Id, device.Name + (device.IsPhysical ? "" : " · 虚拟") + (device.IsConnected ? "" : " · 已断开"));
                found |= device.Id == selected;
            }
        }
        else foreach (var device in source?.PhysicalDisks ?? []) { Add(device.Id, device.Name); found |= device.Id == selected; }
        if (!found) Add(selected, "所选设备暂不可用（保留选择）");
        menu.ShowAt(target);
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
            var minimized = new ToggleSwitch { Header = "开机启动时静默运行", OnContent = "只显示托盘图标", OffContent = "打开主窗口" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(minimized, "StartMinimizedToggle");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(startupNotice, "StartupNotice");
            try
            {
                var startupStatus = StartupRegistration.GetStatus();
                startup.IsOn = startupStatus.HasRegistration;
                minimized.IsOn = startupStatus.StartMinimized;
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
            minimized.IsEnabled = startup.IsEnabled && startup.IsOn;
            startup.Toggled += (_, _) => minimized.IsEnabled = startup.IsEnabled && startup.IsOn;
            startupPanel.Children.Add(minimized);
            startupPanel.Children.Add(new TextBlock
            {
                Text = "登录 Windows 后自动运行；静默启动仍会采集并按偏好显示悬浮条。手动打开应用会显示主窗口。\n若已在 Windows 的“启动应用”中禁用，请在那里重新启用。",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Style = (Style)Application.Current.Resources["Muted"]
            });
            startupPanel.Children.Add(startupNotice);
            var tray = new ToggleSwitch { Header = "关闭主窗口时", OnContent = "保留在托盘继续监控", OffContent = "退出应用", IsOn = _settings.CloseToTray };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tray, "CloseToTrayToggle");
            var panel = new StackPanel { Spacing = 20, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(theme); panel.Children.Add(startupPanel); panel.Children.Add(tray);
            var clickThrough = new ToggleSwitch { Header = "悬浮条鼠标穿透", OnContent = "点击穿过悬浮条", OffContent = "允许拖动与关闭", IsOn = _settings.OverlayClickThrough, IsEnabled = _tray.IsAvailable };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(clickThrough, "OverlayClickThroughToggle");
            var opacity = new Slider { Header = "悬浮条背景不透明度", Minimum = 20, Maximum = 100, StepFrequency = 5, Value = Math.Clamp(_settings.OverlayOpacity * 100, 20, 100), TickFrequency = 20 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(opacity, "OverlayOpacitySlider");
            panel.Children.Add(clickThrough); panel.Children.Add(opacity);
            panel.Children.Add(new TextBlock { Text = "可从托盘菜单“恢复悬浮条交互”退出穿透。穿透时使用半透明底色；恢复交互后使用 Acrylic，滑块调整背景浓度，文字保持清晰。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Style = (Style)Application.Current.Resources["Muted"] });
            panel.Children.Add(new TextBlock { Text = "刷新频率：1 秒\n趋势保留：60 秒\n进程内存：私有工作集\n增强采集仅提升采集器权限", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "设置", Content = new ScrollViewer { Content = panel, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 580 }, PrimaryButtonText = "保存", CloseButtonText = "取消" };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { if (startup.IsEnabled) StartupRegistration.SetEnabled(startup.IsOn, minimized.IsOn); }
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
                var previousClickThrough = _settings.OverlayClickThrough;
                var previousOpacity = _settings.OverlayOpacity;
                _settings.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
                _settings.CloseToTray = tray.IsOn;
                _settings.OverlayClickThrough = clickThrough.IsEnabled && clickThrough.IsOn;
                _settings.OverlayOpacity = opacity.Value / 100;
                if (!_settings.TrySave(out var error))
                {
                    _settings.Theme = previousTheme;
                    _settings.CloseToTray = previousCloseToTray;
                    _settings.OverlayClickThrough = previousClickThrough;
                    _settings.OverlayOpacity = previousOpacity;
                    args.Cancel = true;
                    startupNotice.Title = "设置未完全保存";
                    startupNotice.Severity = InfoBarSeverity.Error;
                    startupNotice.Message = (startup.IsEnabled ? "开机启动设置已应用；" : "") + $"外观和关闭行为保存失败：{error}";
                    startupNotice.IsOpen = true;
                    return;
                }
                if (_overlay is not null && !_overlay.ApplyAppearance(out var appearanceError))
                {
                    args.Cancel = true; clickThrough.IsOn = _settings.OverlayClickThrough;
                    startupNotice.Title = "悬浮条设置"; startupNotice.Message = appearanceError;
                    startupNotice.Severity = InfoBarSeverity.Warning; startupNotice.IsOpen = true;
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
