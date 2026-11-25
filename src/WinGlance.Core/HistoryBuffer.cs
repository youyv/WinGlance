namespace WinGlance.Core;

/// <summary>A bounded, timestamp-based rolling history, including recently exited applications.</summary>
public sealed class HistoryBuffer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<HistoryPoint>> _series = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _retention;
    private readonly int _maxSeries;
    private readonly int _maxPointsPerSeries;
    private DateTimeOffset _latest = DateTimeOffset.MinValue;

    public HistoryBuffer(int seconds = 60, int maxSeries = 2048, int maxPointsPerSeries = 600)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSeries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPointsPerSeries);
        _retention = TimeSpan.FromSeconds(Math.Min(seconds, 60));
        _maxSeries = maxSeries;
        _maxPointsPerSeries = maxPointsPerSeries;
    }

    public void Add(DateTimeOffset timestamp, IReadOnlyList<SoftwareSample> software)
    {
        ArgumentNullException.ThrowIfNull(software);
        lock (_gate)
        {
            // Ignore a delayed frame instead of drawing time backwards or retaining it indefinitely.
            if (timestamp < _latest) return;
            _latest = timestamp;
            var cutoff = timestamp - _retention;
            var present = software.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, points) in _series.ToArray())
            {
                points.RemoveAll(p => p.Timestamp < cutoff);
                if (points.Count == 0) _series.Remove(key);
                else if (!present.Contains(key) && HasValue(points[^1]) && points[^1].Timestamp < timestamp)
                    points.Add(Missing(timestamp));
                if (points.Count > _maxPointsPerSeries) points.RemoveRange(0, points.Count - _maxPointsPerSeries);
            }
            foreach (var item in software)
            {
                if (!_series.TryGetValue(item.Key, out var points))
                {
                    if (_series.Count >= _maxSeries)
                    {
                        var oldest = _series.MinBy(pair => pair.Value[^1].Timestamp).Key;
                        _series.Remove(oldest);
                    }
                    _series[item.Key] = points = [];
                }
                var point = new HistoryPoint(timestamp, item.CpuPercent, item.GpuPercent,
                    item.DownloadBytesPerSecond, item.UploadBytesPerSecond);
                if (points.Count > 0 && HasValue(points[^1]) && timestamp - points[^1].Timestamp > TimeSpan.FromSeconds(2.5))
                    points.Add(Missing(points[^1].Timestamp.AddSeconds(1)));
                if (points.Count > 0 && points[^1].Timestamp == timestamp) points[^1] = point;
                else points.Add(point);
                if (points.Count > _maxPointsPerSeries) points.RemoveRange(0, points.Count - _maxPointsPerSeries);
            }
        }
    }

    private static HistoryPoint Missing(DateTimeOffset timestamp) => new(timestamp, null, null, null, null);
    private static bool HasValue(HistoryPoint point) => point.CpuPercent.HasValue || point.GpuPercent.HasValue
        || point.DownloadBytesPerSecond.HasValue || point.UploadBytesPerSecond.HasValue;

    public IReadOnlyList<HistoryPoint> Get(string key)
    {
        lock (_gate) return _series.TryGetValue(key, out var points) ? points.ToArray() : [];
    }
}
