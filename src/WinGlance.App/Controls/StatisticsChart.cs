using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinGlance.App.ViewModels;
using WinGlance.Core;
using WinGlance.Statistics;
using Windows.Foundation;

namespace WinGlance.App.Controls;

/// <summary>Time-aware, bounded chart. Incomplete buckets never bridge an unobserved interval.</summary>
public sealed class StatisticsChart : Canvas
{
    private IReadOnlyList<StatisticsTrendPoint> _source = [];
    private DateTimeOffset _start, _end;
    private StatisticMetric _metric;
    private StatisticMetric? _second;
    private readonly Brush _accent = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 183, 164));
    public StatisticsChart() { SizeChanged += (_, _) => Draw(); ActualThemeChanged += (_, _) => Draw(); }
    public void SetSeries(IReadOnlyList<StatisticsTrendPoint> source, DateTimeOffset start, DateTimeOffset end,
        StatisticMetric metric, StatisticMetric? second = null)
    {
        _source = source; _start = start; _end = end; _metric = metric; _second = second; Draw();
    }
    private void Draw()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        Children.Clear();
        bool bytes = _metric is StatisticMetric.Download or StatisticMetric.Upload or StatisticMetric.DiskRead or StatisticMetric.DiskWrite;
        bool percent = _metric is StatisticMetric.Cpu or StatisticMetric.Gpu;
        double span = Math.Max(1, (_end - _start).TotalSeconds), width = Math.Max(1, ActualWidth - 2), height = Math.Max(1, ActualHeight - 24);
        var points = new List<(double Start, double End, double? Value, bool Full)>();
        int maxPoints = Math.Clamp((int)(width / 5), 16, 180);
        foreach (var group in _source.Where(p => p.EndUtc > _start && p.StartUtc < _end)
                     .GroupBy(p => Math.Clamp((int)((p.StartUtc - _start).TotalSeconds / span * maxPoints), 0, maxPoints - 1)))
        {
            double sum = 0, valid = 0, observed = 0; bool hasValue = false;
            var first = group.Min(p => p.StartUtc); var last = group.Max(p => p.EndUtc);
            foreach (var p in group)
            {
                var m = p.Metrics.GetValueOrDefault(_metric);
                var b = _second is { } second ? p.Metrics.GetValueOrDefault(second) : null;
                bool measured = bytes ? m?.Bytes is not null && (_second is null || b?.Bytes is not null)
                    : m?.ValidSeconds > 0 && (_second is null || b?.ValidSeconds > 0);
                if (measured)
                {
                    hasValue = true;
                    sum += bytes ? (m!.Bytes ?? m.Integral ?? 0) + (b?.Bytes ?? b?.Integral ?? 0) : m!.Integral ?? 0;
                    valid += _second is null ? m.ValidSeconds : Math.Min(m.ValidSeconds, b!.ValidSeconds);
                }
                observed += Math.Min(p.ObservedSeconds, Math.Max(0, (p.EndUtc - p.StartUtc).TotalSeconds));
            }
            double duration = Math.Max(1, (last - first).TotalSeconds);
            points.Add(((first - _start).TotalSeconds, (last - _start).TotalSeconds,
                hasValue ? bytes ? sum : valid > 0 ? sum / valid : null : null, valid >= duration - .001 && observed >= duration - .001));
        }
        double maximum = percent ? 100 : Math.Max(1, points.Max(p => p.Value) ?? 0) * 1.1;
        var muted = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        for (int i = 0; i < 3; i++)
            Children.Add(new Line { X1 = 0, X2 = width, Y1 = height * i / 2, Y2 = height * i / 2, Stroke = muted, Opacity = .08, StrokeThickness = 1 });
        PointCollection? segment = null; double previousEnd = double.NegativeInfinity; bool previousFull = false;
        foreach (var p in points)
        {
            if (p.Value is not { } value) { segment = null; previousFull = false; continue; }
            double x1 = Math.Clamp(p.Start / span * width, 0, width), x2 = Math.Clamp(p.End / span * width, 0, width);
            double y = height - Math.Clamp(value / maximum, 0, 1) * Math.Max(1, height - 4);
            if (bytes)
            {
                var bar = new Rectangle { Width = Math.Max(2, x2 - x1 - 1), Height = Math.Max(1, height - y), Fill = _accent, Opacity = p.Full ? .75 : .4, RadiusX = 1.5, RadiusY = 1.5 };
                SetLeft(bar, x1); SetTop(bar, y); Children.Add(bar);
            }
            else
            {
                if (segment is null || !p.Full || !previousFull || p.Start - previousEnd > .001)
                {
                    segment = new PointCollection();
                    Children.Add(new Polyline { Points = segment, Stroke = _accent, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
                }
                segment.Add(new Point((x1 + x2) / 2, y));
                var dot = new Ellipse { Width = 3, Height = 3, Fill = _accent, Opacity = .7 };
                SetLeft(dot, (x1 + x2) / 2 - 1.5); SetTop(dot, y - 1.5); Children.Add(dot);
            }
            previousEnd = p.End; previousFull = p.Full;
        }
        Label(_start.LocalDateTime.ToString(span > 86400 ? "MM-dd HH:mm" : "HH:mm"), 0, height + 6, muted);
        var endLabel = new TextBlock { Text = _end.LocalDateTime.ToString(span > 86400 ? "MM-dd HH:mm" : "HH:mm"), FontSize = 10, Foreground = muted, Opacity = .6 };
        endLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); SetLeft(endLabel, Math.Max(0, width - endLabel.DesiredSize.Width)); SetTop(endLabel, height + 6); Children.Add(endLabel);
        string scale = percent ? "100%" : Display.Bytes(maximum);
        ToolTipService.SetToolTip(this, $"纵轴上限 {scale} · {(bytes ? "每柱为对应时段累计量" : "有效观测期间平均值")}\n最多 {maxPoints} 个显示点；空白表示未观测，浅色柱表示覆盖不足。");
        if (!points.Any(p => p.Value is not null)) Label("尚无有效观测", Math.Max(0, width / 2 - 42), Math.Max(0, height / 2 - 8), muted);
        else Label(scale, 4, 0, muted);
    }
    private void Label(string value, double x, double y, Brush brush)
    {
        var label = new TextBlock { Text = value, FontSize = 10, Foreground = brush, Opacity = .6 };
        SetLeft(label, x); SetTop(label, y); Children.Add(label);
    }
}
