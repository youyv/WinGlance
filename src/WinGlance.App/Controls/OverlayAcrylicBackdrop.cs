using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinGlance.App.Controls;

/// <summary>A window-owned Acrylic backdrop whose visual activation persists on a floating meter.</summary>
internal sealed class OverlayAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemBackdropConfiguration? _configuration;
    private bool _updatingPolicy;
    private bool _notificationQueued;
    private string _notificationReason = "initialized";
    private double _opacity = .8;
    private bool _dark;
    private (bool Dark, double Opacity)? _appliedAppearance;

    internal void SetOpacity(double opacity)
    {
        _opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, .2, 1) : .8;
        ApplyOpacity();
    }
    private void ApplyOpacity()
    {
        if (_controller is null) return;
        if (_appliedAppearance == (_dark, _opacity)) return;
        // Custom opacity disables automatic light/dark defaults in the controller.
        // Keep both tint and solid fallback in sync with the WinUI source theme.
        var color = _dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        _controller.TintColor = color;
        _controller.FallbackColor = color;
        _controller.TintOpacity = (float)_opacity;
        _controller.LuminosityOpacity = (float)_opacity;
        _appliedAppearance = (_dark, _opacity);
    }

    public string Status { get; private set; } = "NotConnected";
    public string State { get; private set; } = "Unavailable";
    public string? Error { get; private set; }
    public bool HighContrast { get; private set; }
    public bool IsInputActive => _configuration?.IsInputActive ?? false;
    public event Action<string>? DiagnosticChanged;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        try
        {
            if (!DesktopAcrylicController.IsSupported())
            {
                Status = "Unsupported";
                Notify("unsupported");
                return;
            }

            _target = connectedTarget;
            _configuration = new SystemBackdropConfiguration();
            UpdateConfiguration(connectedTarget, xamlRoot);
            _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
            ApplyOpacity();
            _controller.StateChanged += Controller_StateChanged;
            _controller.SetSystemBackdropConfiguration(_configuration);
            if (!_controller.AddSystemBackdropTarget(connectedTarget))
                throw new InvalidOperationException("The Acrylic controller could not attach to the overlay.");
            Status = "Attached";
            State = _controller.State.ToString();
            Notify("connected");
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException)
        {
            ReleaseController();
            Status = "InitializationFailed";
            Error = $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";
            Notify("initialization-failed");
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (_configuration is null || _updatingPolicy) return;
        UpdateConfiguration(target, xamlRoot);
        Notify("policy-changed");
    }

    private void UpdateConfiguration(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (_configuration is null || _updatingPolicy) return;
        _updatingPolicy = true;
        try
        {
            // WinUI owns the source policy. Copy it so overriding visual activation cannot
            // mutate the framework's default configuration or recurse through its callbacks.
            var source = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
            _configuration.Theme = source.Theme;
            _dark = source.Theme == SystemBackdropTheme.Dark || source.Theme == SystemBackdropTheme.Default &&
                xamlRoot.Content is FrameworkElement element && element.ActualTheme == ElementTheme.Dark;
            _configuration.HighContrastBackgroundColor = source.HighContrastBackgroundColor;
            _configuration.IsHighContrast = HighContrast = source.IsHighContrast;
            _configuration.IsInputActive = true;
            ApplyOpacity();
        }
        finally { _updatingPolicy = false; }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try { base.OnTargetDisconnected(disconnectedTarget); }
        finally
        {
            ReleaseController();
            Status = "Disconnected";
            Notify("disconnected");
        }
    }

    private void Controller_StateChanged(ISystemBackdropControllerWithTargets sender, object args)
    {
        if (DispatcherQueue.HasThreadAccess) ReadControllerState();
        else DispatcherQueue.TryEnqueue(ReadControllerState);
    }

    private void ReadControllerState()
    {
        if (_controller is null) return;
        State = _controller.State.ToString();
        Notify("state-changed");
    }

    private void ReleaseController()
    {
        var controller = _controller;
        var target = _target;
        _controller = null;
        _appliedAppearance = null;
        _target = null;
        _configuration = null;
        State = "Unavailable";
        if (controller is null) return;
        try { controller.StateChanged -= Controller_StateChanged; }
        finally
        {
            try { if (target is not null) controller.RemoveSystemBackdropTarget(target); }
            finally { controller.Dispose(); }
        }
    }

    internal void ReleaseForClose() => ReleaseController();

    private void Notify(string reason)
    {
        // Defer UI fallback changes and diagnostics until the WinUI callback completes.
        _notificationReason = reason;
        if (_notificationQueued) return;
        _notificationQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _notificationQueued = false;
            DiagnosticChanged?.Invoke(_notificationReason);
        });
    }
}
