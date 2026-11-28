using System.Runtime.InteropServices;
using System.ComponentModel;
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
    private bool _preparingToExit;
    private bool _interactionFallback;
    private XamlRoot? _xamlRoot;
    private double _scale = 1;
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
                               FontSize="12" VerticalAlignment="Center"/>
                    <Button x:Name="CloseButton" Grid.Column="1" Content="×" Padding="6,0,6,0" Margin="12,0,0,0"
                            ToolTipService.ToolTip="关闭悬浮条" AutomationProperties.Name="关闭悬浮条"/>
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
        // The existing WM_SETTINGCHANGE/display path schedules appearance recovery.
        // WinRT accessibility/color event subscription can fail on unpackaged desktops.
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
    private void RecoverPosition()
    {
        if (_closed || _preparingToExit) return;
        try
        {
            _fallback.Opacity = IsHighContrast() ? 1 : EffectiveOpacity;
            uint dpi = GetDpiForWindow(_clickThrough?.Handle ?? _windowHandle);
            _scale = dpi > 0 ? dpi / 96d : _root.XamlRoot?.RasterizationScale ?? 1;
            var size = OverlayPlacement.SizeForScale(_scale);
            if (_clickThrough is not null)
            {
                var currentNative = _clickThrough.Position;
                var recovered = OverlayPlacement.Recover(currentNative.X, currentNative.Y, size.Width, size.Height, DesktopWorkAreas.Get());
                _clickThrough.Invalidate();
                RenderClickThrough(recovered.X, recovered.Y);
                return;
            }
            if (AppWindow.Size.Width != size.Width || AppWindow.Size.Height != size.Height)
                AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, size.Height));
            var current = AppWindow.Position;
            var position = OverlayPlacement.Recover(current.X, current.Y, size.Width, size.Height, DesktopWorkAreas.Get());
            if (position.X != current.X || position.Y != current.Y)
                AppWindow.Move(new Windows.Graphics.PointInt32(position.X, position.Y));
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
            if (!_closed && _clickThrough is null) Activate();
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
        _clickThrough.Render(_text.Text, IsDarkTheme(), IsHighContrast(),
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
        if (_visible && !_closed && !_preparingToExit) { RecoverPosition(); Activate(); }
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
                ClickThrough = IsClickThrough,
                BackgroundOpacity = _settings.OverlayOpacity,
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

    public void Update(SystemSample sample)
    {
        if (!_visible || _closed || _preparingToExit) return;
        _text.Text = $"↓ {Display.Rate(sample.DownloadBytesPerSecond)}   ↑ {Display.Rate(sample.UploadBytesPerSecond)}    CPU {Display.Percent(sample.CpuPercent)}   GPU {Display.Percent(sample.GpuPercent)}";
        RefreshClickThrough();
    }

    internal void Update(SystemSample? sample, CollectionPresentation state)
    {
        if (!_visible || _closed || _preparingToExit) return;
        if (state.IsLive && sample is not null) { Update(sample); return; }
        _text.Text = state.CompactStatus;
        RefreshClickThrough();
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
