using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace WinGlance.App.Controls;

public sealed class Sparkline : Canvas
{
    private IReadOnlyList<(DateTimeOffset Timestamp, double? Value)> _values = [];
    private DateTimeOffset _end;
    private readonly Line _baseline = new() { Opacity = 0.12, StrokeThickness = 1 };
    private readonly List<Polyline> _segments = [];
    public Brush Stroke { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Teal);
    public double FixedMaximum { get; set; }
    public Sparkline() { Children.Add(_baseline); SizeChanged += (_, _) => Draw(); }
    public void SetPoints(IEnumerable<(DateTimeOffset Timestamp, double? Value)> values, DateTimeOffset end)
    {
        _end = end;
        _values = values.Where(p => p.Timestamp >= end.AddSeconds(-60) && p.Timestamp <= end).TakeLast(600).ToArray();
        Draw();
    }
    private void Draw()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        _baseline.X2 = ActualWidth;
        _baseline.Y1 = _baseline.Y2 = ActualHeight - 1;
        _baseline.Stroke = Stroke;
        var maximum = FixedMaximum > 0 ? FixedMaximum : Math.Max(1, _values.Count == 0 ? 0 : _values.Max(v => v.Value ?? 0) * 1.1);
        var points = new List<PointCollection>();
        PointCollection? segment = null;
        for (int i = 0; i < _values.Count; i++)
        {
            if (_values[i].Value is not double value || !double.IsFinite(value)) { segment = null; continue; }
            if (i > 0 && (_values[i].Timestamp - _values[i - 1].Timestamp).TotalSeconds > 2.5) segment = null;
            if (segment is null) { segment = new PointCollection(); points.Add(segment); }
            var x = (60 - (_end - _values[i].Timestamp).TotalSeconds) * ActualWidth / 60;
            var y = 2 + (ActualHeight - 4) * (1 - Math.Clamp(value / maximum, 0, 1));
            segment.Add(new Point(x, y));
        }
        while (_segments.Count > points.Count)
        {
            Children.Remove(_segments[^1]);
            _segments.RemoveAt(_segments.Count - 1);
        }
        for (int i = 0; i < points.Count; i++)
        {
            if (i == _segments.Count)
            {
                var line = new Polyline { StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
                _segments.Add(line); Children.Add(line);
            }
            _segments[i].Stroke = Stroke;
            // Build the point collection while detached, then invalidate the retained line once.
            _segments[i].Points = points[i];
        }
    }
}
