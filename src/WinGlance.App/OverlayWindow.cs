using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.UI.ViewManagement;
using WinGlance.App.Controls;
using WinGlance.App.ViewModels;
using WinGlance.Core;
using WinGlance.App.Services;

namespace WinGlance.App;

public sealed class OverlayWindow : Window
{
    private readonly Grid _root;
    private readonly Border _fallback;
    private readonly TextBlock _text;
    private readonly Button _closeButton;
    private readonly OverlayAcrylicBackdrop _backdrop = new();
    private readonly UISettings _uiSettings = new();
    private WindowActivationState _activation = WindowActivationState.Deactivated;
    private bool _closed;
    private bool _visible;
    private bool _hasBeenShown;
    private bool _preparingToExit;
    private bool _interactionFallback;
    private bool _textScaleSubscribed;
    private XamlRoot? _xamlRoot;
    private double _scale = 1;
    private double _textScale = 1;
    private double _availableWidthDip = 510;
    private double _availableHeightDip = double.PositiveInfinity;
    private SystemSample? _sample;
    private bool _isLive;
    private string _compactStatus = "等待数据";
    private OverlayPresentation _presentation = OverlayPresentation.Create(OverlayPreset.Standard, null, false, "等待数据");
    private ClickThroughOverlay? _clickThrough;
    public bool IsClickThrough => _clickThrough is not null;
    public bool IsVisible => _visible && !_closed;
    public event Action<bool>? ClickThroughChanged;
    public event Action<string>? AppearanceFailed;
    public event Action? Hidden;
    private readonly nint _windowHandle;
    private readonly AppSettings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _positionTimer;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "WinGlance 悬浮条";
        _root = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent">
                <Border x:Name="Fallback" Background="{ThemeResource SolidBackgroundFillColorBaseBrush}" IsHitTestVisible="False"/>
                <Grid Padding="16,10,12,10">
                    <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <TextBlock x:Name="StatusText" Text="WinGlance · 等待采样" Foreground="{ThemeResource TextFillColorPrimaryBrush}"
                               FontFamily="Consolas" FontSize="12" LineHeight="20" IsTextScaleFactorEnabled="False"
                               LineStackingStrategy="BlockLineHeight" VerticalAlignment="Center"/>
                    <Button x:Name="CloseButton" Grid.Column="1" Padding="0" Margin="12,0,0,0"
                            Width="36" MinWidth="36" Height="32" MinHeight="32" VerticalAlignment="Center"
                            IsTextScaleFactorEnabled="False" FontSize="16"
                            ToolTipService.ToolTip="关闭悬浮条" AutomationProperties.Name="关闭悬浮条">
                        <TextBlock Text="×" FontFamily="Segoe UI Symbol" FontSize="16" IsTextScaleFactorEnabled="False"/>
                    </Button>
                </Grid>
            </Grid>
            """);
        _fallback = (Border)_root.FindName("Fallback");
        _text = (TextBlock)_root.FindName("StatusText");
        _closeButton = (Button)_root.FindName("CloseButton");
        _closeButton.Click += CloseButton_Click;
        Content = _root;
        ApplyTheme(settings.Theme);
        _root.ActualThemeChanged += Root_ActualThemeChanged;
        _backdrop.DiagnosticChanged += Backdrop_DiagnosticChanged;
        Activated += Window_Activated;
        Closed += Window_Closed;
        // WinUI attaches/disconnects the target and maintains the source theme/accessibility policy.
        SystemBackdrop = _backdrop;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_text);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true; presenter.IsResizable = false;
            presenter.IsMaximizable = false; presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        _positionTimer = DispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(600);
        _positionTimer.IsRepeating = false;
        _positionTimer.Tick += PositionTimer_Tick;
        // Some unpackaged desktops reject this WinRT event. WM_SETTINGCHANGE and
        // reopening still reread the value through the existing recovery path.
        try { _uiSettings.TextScaleFactorChanged += UiSettings_TextScaleFactorChanged; _textScaleSubscribed = true; }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or NotSupportedException) { }
        AppWindow.Move(new Windows.Graphics.PointInt32(settings.OverlayX ?? 80, settings.OverlayY ?? 80));
        RecoverPosition();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        _root.Loaded += Root_Loaded;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_preparingToExit || _closed) return;
        args.Cancel = true;
        HideOverlay();
    }
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) ScheduleRecovery();
    }
    private void Root_Loaded(object sender, RoutedEventArgs args)
    {
        if (_closed || _xamlRoot is not null) return;
        _xamlRoot = _root.XamlRoot;
        if (_xamlRoot is not null) _xamlRoot.Changed += XamlRoot_Changed;
        ScheduleRecovery();
    }
    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!_closed && sender.RasterizationScale != _scale) ScheduleRecovery();
    }
    private void PositionTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_visible || _closed || _preparingToExit) return;
        RecoverPosition(); SavePosition();
    }
    internal void ScheduleRecovery()
    {
        if (!_visible || _closed || _preparingToExit) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_visible && !_closed && !_preparingToExit) { _positionTimer.Stop(); _positionTimer.Start(); }
        });
    }
    private void UiSettings_TextScaleFactorChanged(UISettings sender, object args) => ScheduleRecovery();

    private double ReadTextScale()
    {
        // Isolated diagnostic instances inject only their own factor; the real
        // Windows text size and ordinary user instances are never modified.
        if (AppPaths.IsDiagnostic)
        {
            try
            {
                string path = Path.Combine(AppPaths.DataDirectory, "overlay-text-scale.txt");
                if (File.Exists(path) && double.TryParse(File.ReadAllText(path), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double injected))
                    return OverlayPresentation.NormalizeTextScale(injected);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { return OverlayPresentation.NormalizeTextScale(_uiSettings.TextScaleFactor); }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or NotSupportedException) { return 1; }
    }
    private void RecoverPosition()
    {
        if (_closed || _preparingToExit) return;
        try
        {
            var previousEnvironment = (_textScale, _scale, _availableWidthDip, _availableHeightDip);
            _textScale = ReadTextScale();
            _fallback.Opacity = IsHighContrast() ? 1 : EffectiveOpacity;
            uint dpi = GetDpiForWindow(_clickThrough?.Handle ?? _windowHandle);
            _scale = dpi > 0 ? dpi / 96d : _root.XamlRoot?.RasterizationScale ?? 1;
            var areas = DesktopWorkAreas.Get();
            var anchor = _clickThrough?.Position ?? (AppWindow.Position.X, AppWindow.Position.Y);
            var nearest = areas.OrderBy(area =>
            {
                double dx = Math.Clamp(anchor.X, area.X, area.X + area.Width) - (double)anchor.X;
                double dy = Math.Clamp(anchor.Y, area.Y, area.Y + area.Height) - (double)anchor.Y;
                return dx * dx + dy * dy;
            }).FirstOrDefault();
            _availableWidthDip = areas.Count == 0 ? 510 : nearest.Width / _scale;
            _availableHeightDip = areas.Count == 0 ? double.PositiveInfinity : nearest.Height / _scale;
            bool environmentChanged = previousEnvironment != (_textScale, _scale, _availableWidthDip, _availableHeightDip);
            RebuildPresentation();
            var size = OverlayPlacement.SizeForScale(_scale, _presentation.WidthDip, _presentation.HeightDip);
            if (_clickThrough is not null)
            {
                var currentNative = _clickThrough.Position;
                var recovered = OverlayPlacement.Recover(currentNative.X, currentNative.Y, size.Width, size.Height, areas);
                _clickThrough.Invalidate();
                RenderClickThrough(recovered.X, recovered.Y);
                if (environmentChanged) RecordBackdropState("layout-environment-changed");
                return;
            }
            if (AppWindow.Size.Width != size.Width || AppWindow.Size.Height != size.Height)
                AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, size.Height));
            var current = AppWindow.Position;
            var position = OverlayPlacement.Recover(current.X, current.Y, size.Width, size.Height, areas);
            if (position.X != current.X || position.Y != current.Y)
                AppWindow.Move(new Windows.Graphics.PointInt32(position.X, position.Y));
            if (environmentChanged) RecordBackdropState("layout-environment-changed");
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or Win32Exception)
        {
            if (_clickThrough is not null) RecoverInteraction(ex.Message);
        }
    }
    internal void PrepareForExit()
    {
        if (_closed || _preparingToExit) return;
        _positionTimer.Stop();
        SavePosition(persist: false);
        _preparingToExit = true;
    }
    internal void SavePosition(bool persist = true)
    {
        if (_closed) return;
        // The native window can already be gone when the WinUI Closed notification arrives.
        // In that case keep the last position captured by the debounce timer.
        try
        {
            var position = _clickThrough?.Position ?? (AppWindow.Position.X, AppWindow.Position.Y);
            if (_settings.OverlayX == position.X && _settings.OverlayY == position.Y) return;
            _settings.OverlayX = position.X; _settings.OverlayY = position.Y;
            if (persist && !_preparingToExit) _settings.Save();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or Win32Exception) { }
    }

    public void ApplyTheme(string theme)
    {
        if (_closed) return;
        _root.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        RefreshClickThrough();
    }

    public bool ShowOverlay(out string? error)
    {
        if (_closed || _preparingToExit) { error = "悬浮条已关闭。"; return false; }
        try
        {
            _visible = true;
            bool applied = ApplyAppearance(out error);
            RecoverPosition();
            if (!_closed && _clickThrough is null) ShowWithoutStealingFocus();
            ScheduleRecovery();
            RecordBackdropState("shown");
            return applied;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or Win32Exception or InvalidOperationException)
        {
            _visible = false;
            _positionTimer.Stop();
            _clickThrough?.Hide();
            try { AppWindow.Hide(); }
            catch (Exception hideError) when (hideError is COMException or ObjectDisposedException) { }
            error = "悬浮条未能显示：" + ex.Message;
            RecordBackdropState("show-failed");
            AppearanceFailed?.Invoke(error);
            return false;
        }
    }
    private void CloseButton_Click(object sender, RoutedEventArgs args) => HideOverlay();
    private void ShowWithoutStealingFocus()
    {
        if (!_hasBeenShown) { Activate(); _hasBeenShown = true; }
        else AppWindow.Show(false);
    }
    public void HideOverlay()
    {
        if (!_visible || _closed || _preparingToExit) return;
        SavePosition();
        _visible = false;
        _positionTimer.Stop();
        _clickThrough?.Hide();
        AppWindow.Hide();
        RecordBackdropState("hidden");
        Hidden?.Invoke();
    }
    internal void SetInteractionFallback(bool fallback)
    {
        _interactionFallback = fallback;
        if (fallback && _clickThrough is not null) RecoverInteraction(null);
    }
    public bool ApplyAppearance(out string? error)
    {
        error = null;
        if (_closed || _preparingToExit) { error = "悬浮条已关闭。"; return false; }
        try
        {
            _settings.OverlayOpacity = EffectiveOpacity;
            _settings.OverlayPreset = OverlayPresentation.Normalize(_settings.OverlayPreset);
            RebuildPresentation();
            _backdrop.SetOpacity(_settings.OverlayOpacity);
            _fallback.Opacity = IsHighContrast() ? 1 : _settings.OverlayOpacity;
            if (!_visible) { RecordBackdropState("hidden-appearance-changed"); return true; }
            if (_settings.OverlayClickThrough && !_interactionFallback)
            {
                bool newlyCreated = _clickThrough is null;
                if (newlyCreated)
                {
                    if (AppPaths.InjectFault("overlay")) throw new InvalidOperationException("诊断：模拟穿透窗口创建失败");
                    var position = AppWindow.Position;
                    _clickThrough = new ClickThroughOverlay(position.X, position.Y, ScheduleRecovery, HideOverlay);
                }
                var current = _clickThrough!.Position;
                RenderClickThrough(current.X, current.Y);
                AppWindow.Hide();
                if (newlyCreated) ClickThroughChanged?.Invoke(true);
            }
            else if (_clickThrough is not null) RecoverInteraction(null);
            RecoverPosition();
            RecordBackdropState("appearance-changed");
            return true;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or Win32Exception or InvalidOperationException)
        {
            error = "鼠标穿透不可用，已恢复悬浮条交互：" + ex.Message;
            RecoverInteraction(error);
            return false;
        }
    }
    private void RenderClickThrough(int x, int y)
    {
        if (_clickThrough is null) return;
        uint dpi = GetDpiForWindow(_clickThrough.Handle);
        if (dpi > 0) _scale = dpi / 96d;
        _clickThrough.Render(_presentation, IsDarkTheme(), IsHighContrast(),
            _settings.OverlayOpacity, x, y, _scale);
    }
    private void RefreshClickThrough()
    {
        if (!_visible || _closed || _preparingToExit || _clickThrough is null) return;
        try
        {
            var position = _clickThrough.Position;
            RenderClickThrough(position.X, position.Y);
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or Win32Exception or InvalidOperationException)
        {
            RecoverInteraction("鼠标穿透绘制失败，已恢复悬浮条交互：" + ex.Message);
        }
    }
    private void RecoverInteraction(string? error)
    {
        bool changed = _clickThrough is not null;
        if (_clickThrough is not null)
        {
            try
            {
                var position = _clickThrough.Position;
                AppWindow.Move(new Windows.Graphics.PointInt32(position.X, position.Y));
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException or Win32Exception) { }
            _clickThrough.Dispose(); _clickThrough = null;
        }
        if (error is not null) _interactionFallback = true;
        if (_visible && !_closed && !_preparingToExit) { RecoverPosition(); ShowWithoutStealingFocus(); }
        if (changed) ClickThroughChanged?.Invoke(false);
        if (error is not null) AppearanceFailed?.Invoke(error);
    }
    private void RefreshColors()
    {
        _clickThrough?.Invalidate();
        RefreshClickThrough();
    }
    private void Root_ActualThemeChanged(FrameworkElement sender, object args) => RefreshColors();
    private double EffectiveOpacity => double.IsFinite(_settings.OverlayOpacity) ? Math.Clamp(_settings.OverlayOpacity, .2, 1) : .8;
    private bool IsDarkTheme()
    {
        if (_root.RequestedTheme != ElementTheme.Default) return _root.RequestedTheme == ElementTheme.Dark;
        try { return _uiSettings.GetColorValue(UIColorType.Foreground).R > 128; }
        catch (COMException) { return _root.ActualTheme == ElementTheme.Dark; }
    }
    private bool IsHighContrast()
    {
        var state = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfo(0x0042, state.Size, ref state, 0)
            ? (state.Flags & 1) != 0 : _backdrop.HighContrast;
    }

    private void Backdrop_DiagnosticChanged(string reason)
    {
        if (_closed) return;
        _fallback.Visibility = _backdrop.Status == "Attached" ? Visibility.Collapsed : Visibility.Visible;
        RecordBackdropState(reason);
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        _activation = args.WindowActivationState;
        RecordBackdropState("activation-changed");
    }

    private void RecordBackdropState(string reason)
    {
        if (_preparingToExit) return;
        // One overwritten snapshot; metric refreshes do not write to disk.
        try
        {
            var directory = AppPaths.DataDirectory;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "overlay-backdrop.json"), JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId,
                Reason = reason,
                Status = _closed ? "Closed" : !_visible ? "Hidden" : _backdrop.Status,
                State = _backdrop.State,
                Kind = "Thin",
                Lifecycle = "Window.SystemBackdrop",
                WindowActivation = _activation.ToString(),
                IsInputActive = _backdrop.IsInputActive,
                RequestedTheme = _root.RequestedTheme.ToString(),
                ActualTheme = _root.ActualTheme.ToString(),
                AdvancedEffectsEnabled = _uiSettings.AdvancedEffectsEnabled,
                HighContrast = IsHighContrast(),
                Error = _backdrop.Error,
                RasterizationScale = _scale,
                TextScaleFactor = _presentation.TextScale,
                FontSizeDip = _presentation.FontSizeDip,
                LineHeightDip = _presentation.LineHeightDip,
                TextAutomaticScaleEnabled = _text.IsTextScaleFactorEnabled,
                CloseAutomaticScaleEnabled = _closeButton.IsTextScaleFactorEnabled,
                CloseFontSizeDip = _closeButton.FontSize,
                CloseWidthDip = _closeButton.Width,
                NativeFontPixelHeight = _clickThrough?.FontPixelHeight,
                NativeLinePixelHeight = _clickThrough?.LinePixelHeight,
                AvailableWidthDip = _availableWidthDip,
                AvailableHeightDip = double.IsFinite(_availableHeightDip) ? (double?)_availableHeightDip : null,
                SpaceLimited = _presentation.IsSpaceLimited,
                SpaceLimitMessage = _presentation.IsSpaceLimited ? OverlayPresentation.SpaceLimitMessage : null,
                ClickThrough = IsClickThrough,
                BackgroundOpacity = _settings.OverlayOpacity,
                Preset = OverlayPresentation.Normalize(_settings.OverlayPreset).ToString(),
                LayoutWidth = _presentation.WidthDip,
                LayoutHeight = _presentation.HeightDip,
                DisplayText = _presentation.Text,
                RenderMode = IsClickThrough ? "NativeLayeredSolid" : "WinUIAcrylic"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ObjectDisposedException) { }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        if (!_preparingToExit) SavePosition();
        _positionTimer?.Stop();
        if (_positionTimer is not null) _positionTimer.Tick -= PositionTimer_Tick;
        _closed = true; _visible = false;
        _clickThrough?.Dispose(); _clickThrough = null;
        if (!_preparingToExit) RecordBackdropState("closed");
        Activated -= Window_Activated;
        Closed -= Window_Closed;
        _root.Loaded -= Root_Loaded;
        _root.ActualThemeChanged -= Root_ActualThemeChanged;
        _closeButton.Click -= CloseButton_Click;
        if (_textScaleSubscribed)
        {
            try { _uiSettings.TextScaleFactorChanged -= UiSettings_TextScaleFactorChanged; }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException or NotSupportedException) { }
            _textScaleSubscribed = false;
        }
        try
        {
            AppWindow.Changed -= AppWindow_Changed;
            AppWindow.Closing -= AppWindow_Closing;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException) { }
        try { if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRoot_Changed; }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException) { }
        _xamlRoot = null;
        _backdrop.DiagnosticChanged -= Backdrop_DiagnosticChanged;
        // Hide/show reuses the native composition target. Only real shutdown disconnects it.
        try { SystemBackdrop = null; }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException) { }
        try { _backdrop.ReleaseForClose(); }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException) { }
    }

    private void RebuildPresentation()
    {
        _presentation = OverlayPresentation.Create(_settings.OverlayPreset, _sample, _isLive, _compactStatus,
            _availableWidthDip, _textScale, _availableHeightDip, _scale);
        if (_text.Text != _presentation.Text) _text.Text = _presentation.Text;
        if (_text.FontSize != _presentation.FontSizeDip) _text.FontSize = _presentation.FontSizeDip;
        if (_text.LineHeight != _presentation.LineHeightDip) _text.LineHeight = _presentation.LineHeightDip;
        string help = _presentation.IsSpaceLimited ? OverlayPresentation.SpaceLimitMessage : "";
        if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(_root) != help)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(_root, help);
            ToolTipService.SetToolTip(_text, _presentation.IsSpaceLimited ? help : null);
        }
    }
    private void PresentSample()
    {
        if (!_visible || _closed || _preparingToExit) return;
        var previous = _presentation;
        RebuildPresentation();
        if (_presentation.WidthDip != previous.WidthDip || _presentation.HeightDip != previous.HeightDip)
            RecoverPosition();
        RefreshClickThrough();
    }
    public void Update(SystemSample sample)
    {
        if (_closed || _preparingToExit) return;
        _sample = sample; _isLive = true; _compactStatus = "实时";
        PresentSample();
    }
    internal void Update(SystemSample? sample, CollectionPresentation state)
    {
        if (_closed || _preparingToExit) return;
        _sample = sample; _isLive = state.IsLive; _compactStatus = state.CompactStatus;
        PresentSample();
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct HighContrastInfo
    {
        public uint Size, Flags;
        public nint DefaultScheme;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrastInfo value, uint flags);
}
