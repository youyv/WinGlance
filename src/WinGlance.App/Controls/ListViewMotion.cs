using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using WinGlance.App.ViewModels;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace WinGlance.App.Controls;

/// <summary>Moves existing visible rows without replaying opacity or entrance animations.</summary>
internal static class ListViewMotion
{
    private static readonly UISettings Settings = new();
    private static string? Key(object value) => value switch { SoftwareRow row => row.Key, StatisticsRow row => row.Key, _ => null };

    public static Dictionary<string, double>? Capture(ListView list)
    {
        if (Services.AppPaths.DisableMotion || !Settings.AnimationsEnabled || list.ItemsPanelRoot is null) return null;
        var positions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in list.ItemsPanelRoot.Children.OfType<ListViewItem>())
        {
            if (Key(container.Content) is not { } key) continue;
            double y = container.TransformToVisual(list).TransformPoint(new Point()).Y;
            if (y + container.ActualHeight >= 0 && y <= list.ActualHeight)
                positions[key] = y;
        }
        return positions;
    }

    public static void Animate(ListView list, Dictionary<string, double>? positions)
    {
        if (positions is null || positions.Count == 0 || !Settings.AnimationsEnabled) return;
        // Commit the entire collection diff once. Per-row layouts cause a visible cascade.
        list.UpdateLayout();
        if (list.ItemsPanelRoot is null) return;
        foreach (var container in list.ItemsPanelRoot.Children.OfType<ListViewItem>())
        {
            if (Key(container.Content) is not { } key || !positions.TryGetValue(key, out double oldY)) continue;
            double newY = container.TransformToVisual(list).TransformPoint(new Point()).Y;
            float delta = (float)(oldY - newY);
            if (Math.Abs(delta) < 0.5f || Math.Abs(delta) > list.ActualHeight) continue;
            ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            var visual = ElementCompositionPreview.GetElementVisual(container);
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.StopAnimation("Translation");
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(220);
            animation.InsertKeyFrame(0, new Vector3(0, delta, 0));
            animation.InsertKeyFrame(1, Vector3.Zero,
                visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.7f), new Vector2(0.3f, 1)));
            visual.StartAnimation("Translation", animation);
        }
    }

    public static void Reset(UIElement container)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(container, true);
        var visual = ElementCompositionPreview.GetElementVisual(container);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.StopAnimation("Translation");
    }
}
