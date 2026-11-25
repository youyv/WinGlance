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
    private readonly SolidColorBrush _muted = new();
    private readonly List<Line> _grid = [];
    private readonly List<Rectangle> _bars = [];
    private readonly List<Ellipse> _dots = [];
    private readonly List<Polyline> _segments = [];
    private readonly List<TextBlock> _labels = [];
    private int _labelCount;
    public StatisticsChart() { SizeChanged += (_, _) => Draw(); ActualThemeChanged += (_, _) => Draw(); }
    public void SetSeries(IReadOnlyList<StatisticsTrendPoint> source, DateTimeOffset start, DateTimeOffset end,
        StatisticMetric metric, StatisticMetric? second = null)
    {
        _source = source; _start = start; _end = end; _metric = metric; _second = second; Draw();
    }
    private void Draw()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        int bars = 0, dots = 0, segments = 0; _labelCount = 0;
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
        _muted.Color = ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var muted = _muted;
        for (int i = 0; i < 3; i++)
        {
            var line = Get(_grid, i, () => new Line { Stroke = muted, Opacity = .08, StrokeThickness = 1 });
            line.X1 = 0; line.X2 = width; line.Y1 = line.Y2 = height * i / 2;
        }
        PointCollection? segment = null;
        Polyline? segmentLine = null;
        void FinishSegment() { if (segmentLine is not null && segment is not null) segmentLine.Points = segment; segmentLine = null; segment = null; }
        double previousEnd = double.NegativeInfinity; bool previousFull = false;
        foreach (var p in points)
        {
            if (p.Value is not { } value) { FinishSegment(); previousFull = false; continue; }
            double x1 = Math.Clamp(p.Start / span * width, 0, width), x2 = Math.Clamp(p.End / span * width, 0, width);
            double y = height - Math.Clamp(value / maximum, 0, 1) * Math.Max(1, height - 4);
            if (bytes)
            {
                var bar = Get(_bars, bars++, () => new Rectangle { Fill = _accent, RadiusX = 1.5, RadiusY = 1.5 });
                bar.Width = Math.Max(2, x2 - x1 - 1); bar.Height = Math.Max(1, height - y); bar.Opacity = p.Full ? .75 : .4;
                SetLeft(bar, x1); SetTop(bar, y);
            }
            else
            {
                if (segment is null || !p.Full || !previousFull || p.Start - previousEnd > .001)
                {
                    FinishSegment();
                    segmentLine = Get(_segments, segments++, () => new Polyline { Stroke = _accent, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
                    // Populate while detached so each retained shape is invalidated once per draw.
                    segment = new PointCollection();
                }
                segment.Add(new Point((x1 + x2) / 2, y));
                var dot = Get(_dots, dots++, () => new Ellipse { Width = 3, Height = 3, Fill = _accent, Opacity = .7 });
                SetLeft(dot, (x1 + x2) / 2 - 1.5); SetTop(dot, y - 1.5);
            }
            previousEnd = p.End; previousFull = p.Full;
        }
        FinishSegment();
        Label(_start.LocalDateTime.ToString(span > 86400 ? "MM-dd HH:mm" : "HH:mm"), 0, height + 6, muted);
        var endLabel = Label(_end.LocalDateTime.ToString(span > 86400 ? "MM-dd HH:mm" : "HH:mm"), 0, height + 6, muted);
        endLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); SetLeft(endLabel, Math.Max(0, width - endLabel.DesiredSize.Width));
        string scale = percent ? "100%" : Display.Bytes(maximum);
        ToolTipService.SetToolTip(this, $"纵轴上限 {scale} · {(bytes ? "每柱为对应时段累计量" : "有效观测期间平均值")}\n最多 {maxPoints} 个显示点；空白表示未观测，浅色柱表示覆盖不足。");
        if (!points.Any(p => p.Value is not null)) Label("尚无有效观测", Math.Max(0, width / 2 - 42), Math.Max(0, height / 2 - 8), muted);
        else Label(scale, 4, 0, muted);
        HideUnused(_bars, bars); HideUnused(_dots, dots); HideUnused(_segments, segments); HideUnused(_labels, _labelCount);
    }
    private TextBlock Label(string value, double x, double y, Brush brush)
    {
        var label = Get(_labels, _labelCount++, () => new TextBlock { FontSize = 10, Foreground = brush, Opacity = .6 });
        label.Text = value; SetZIndex(label, 3); SetLeft(label, x); SetTop(label, y); return label;
    }
    private T Get<T>(List<T> pool, int index, Func<T> create) where T : UIElement
    {
        if (index == pool.Count) { var element = create(); pool.Add(element); Children.Add(element); }
        var result = pool[index]; result.Visibility = Visibility.Visible; return result;
    }
    private static void HideUnused<T>(List<T> pool, int used) where T : UIElement
    {
        for (int i = used; i < pool.Count; i++) pool[i].Visibility = Visibility.Collapsed;
    }
}
