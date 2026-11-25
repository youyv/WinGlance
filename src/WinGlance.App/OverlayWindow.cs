using System.Runtime.InteropServices;
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
    private readonly OverlayAcrylicBackdrop _backdrop = new();
    private readonly UISettings _uiSettings = new();
    private WindowActivationState _activation = WindowActivationState.Deactivated;
    private bool _closed;
    private readonly AppSettings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _positionTimer;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
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
        ((Button)_root.FindName("CloseButton")).Click += (_, _) => Close();
        Content = _root;
        ApplyTheme(settings.Theme);
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(510, 58));
        _positionTimer = DispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(600);
        _positionTimer.IsRepeating = false;
        _positionTimer.Tick += (_, _) => { RecoverPosition(); SavePosition(); };
        AppWindow.Move(new Windows.Graphics.PointInt32(settings.OverlayX ?? 80, settings.OverlayY ?? 80));
        RecoverPosition();
        AppWindow.Changed += (_, args) =>
        {
            if (!_closed && (args.DidPositionChange || args.DidSizeChange)) { _positionTimer.Stop(); _positionTimer.Start(); }
        };
    }

    internal void ScheduleRecovery() => DispatcherQueue.TryEnqueue(() => { if (!_closed) { _positionTimer.Stop(); _positionTimer.Start(); } });
    private void RecoverPosition()
    {
        if (_closed) return;
        var areas = DesktopWorkAreas.Get();
        var position = OverlayPlacement.Recover(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, areas);
        if (position.X != AppWindow.Position.X || position.Y != AppWindow.Position.Y)
            AppWindow.Move(new Windows.Graphics.PointInt32(position.X, position.Y));
    }
    internal void SavePosition()
    {
        if (_closed) return;
        var position = AppWindow.Position;
        if (_settings.OverlayX == position.X && _settings.OverlayY == position.Y) return;
        _settings.OverlayX = position.X; _settings.OverlayY = position.Y; _settings.Save();
    }

    public void ApplyTheme(string theme)
    {
        if (_closed) return;
        _root.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
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
        // One overwritten snapshot; metric refreshes do not write to disk.
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "overlay-backdrop.json"), JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                ProcessId = Environment.ProcessId,
                Reason = reason,
                Status = _closed ? "Closed" : _backdrop.Status,
                State = _backdrop.State,
                Kind = "Thin",
                Lifecycle = "Window.SystemBackdrop",
                WindowActivation = _activation.ToString(),
                IsInputActive = _backdrop.IsInputActive,
                RequestedTheme = _root.RequestedTheme.ToString(),
                ActualTheme = _root.ActualTheme.ToString(),
                AdvancedEffectsEnabled = _uiSettings.AdvancedEffectsEnabled,
                HighContrast = _backdrop.HighContrast,
                Error = _backdrop.Error
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ObjectDisposedException) { }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        SavePosition();
        _positionTimer?.Stop();
        _closed = true;
        RecordBackdropState("closed");
        Activated -= Window_Activated;
        Closed -= Window_Closed;
        _backdrop.DiagnosticChanged -= Backdrop_DiagnosticChanged;
        // Controller release is performed by SystemBackdrop.OnTargetDisconnected.
    }

    public void Update(SystemSample sample)
    {
        if (_closed) return;
        _text.Text = $"↓ {Display.Rate(sample.DownloadBytesPerSecond)}   ↑ {Display.Rate(sample.UploadBytesPerSecond)}    CPU {Display.Percent(sample.CpuPercent)}   GPU {Display.Percent(sample.GpuPercent)}";
    }
}
