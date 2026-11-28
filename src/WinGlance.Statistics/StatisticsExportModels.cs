using System.Collections.ObjectModel;
using WinGlance.Core;

namespace WinGlance.Statistics;

public enum StatisticsExportFormat { Json, Csv }
public enum StatisticsExportPhase { Selecting, Capturing, Writing, Committing, Completed, Cancelled, Failed }

public sealed record StatisticsExportRequest(StatisticsRange Range, bool HideSystem, string Search,
    string View, string Sort, bool Descending, DateTimeOffset RequestedAtUtc);
public sealed record StatisticsExportResult(StatisticsExportPhase Phase, string? DestinationPath = null,
    string? RecoveryPath = null, string? Error = null, string? Warning = null);
public sealed record StatisticsExportScope(bool HideSystem, string Search, string View, string Sort, bool Descending,
    int AllApplicationsCount, int ExportedApplicationsCount, string DenominatorScope,
    decimal? AttributableNetworkBytes, decimal? AttributableDiskBytes);
public sealed record StatisticsExportMetric(string ValueUnit, string IntegralUnit,
    double? Integral, double ValidSeconds, double? Average, double? Peak, long? Bytes, long? CpuTicks,
    double Coverage, DateTimeOffset? PeakUtc);
public sealed record StatisticsExportSystem(double ObservedSeconds,
    IReadOnlyDictionary<StatisticMetric, StatisticsExportMetric> Metrics,
    long? TotalMemoryBytes, long? DedicatedGpuCapacityBytes, string? NetworkScope,
    EtwQualityAccounting? EtwQuality, string? Warning);
public sealed record StatisticsExportApplication(string AppId, string DisplayName, string ProcessName,
    string? Publisher, SoftwareCategory Category, bool IsRunning, DateTimeOffset LastSeenUtc, double ObservedSeconds,
    IReadOnlyDictionary<StatisticMetric, StatisticsExportMetric> Metrics, decimal? NetworkShare, decimal? DiskShare);

/// <summary>Detached values captured in one statistics-writer operation. No process paths or icons.</summary>
public sealed record StatisticsExportSnapshot(int SchemaVersion, DateTimeOffset RequestedAtUtc, DateTimeOffset AsOfUtc,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset? LastRecordedUtc, DateTimeOffset? LastResetUtc,
    DateTimeOffset? LastSavedUtc, Guid SessionId, string TimeZoneId, StatisticsRange Range,
    StatisticsExportScope Scope, StatisticsExportSystem System, IReadOnlyList<StatisticsExportApplication> Applications)
{
    public string ObservationBasis => "observed_intervals_only";
    public double CpuTickSeconds => 0.0000001;
}

public sealed partial class StatisticsService
{
    public Task<StatisticsExportSnapshot> CaptureExportAsync(StatisticsExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Enqueue(() => CaptureExport(request, cancellationToken), cancellationToken, "captureExport");
    }

    private StatisticsExportSnapshot CaptureExport(StatisticsExportRequest request, CancellationToken token)
    {
        if (request.View is not ("overview" or "network" or "compute" or "disk")) throw new ArgumentException("Unknown statistics view.", nameof(request));
        if (request.Sort is not ("name" or "first" or "second" or "third" or "fourth")) throw new ArgumentException("Unknown statistics sort.", nameof(request));
        var context = CreateQueryContext(request.Range, token);
        var snapshot = Query(context, useCache: false, includeTrend: false);
        string search = (request.Search ?? "").Trim();
        var all = snapshot.Applications;
        var filtered = all.Where(app => (!request.HideSystem || app.Software.Category != SoftwareCategory.WindowsSystem) &&
            (search.Length == 0 || app.Software.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             app.Software.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (app.Software.FilePath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)));
        IOrderedEnumerable<ApplicationStatistics> ordered;
        if (request.Sort == "name") ordered = request.Descending
            ? filtered.OrderByDescending(app => app.Software.DisplayName) : filtered.OrderBy(app => app.Software.DisplayName);
        else
        {
            var present = filtered.OrderBy(app => ExportSortValue(app, request.View, request.Sort) is null);
            ordered = request.Descending ? present.ThenByDescending(app => ExportSortValue(app, request.View, request.Sort))
                : present.ThenBy(app => ExportSortValue(app, request.View, request.Sort));
        }
        decimal? denominator(StatisticMetric first, StatisticMetric second)
        {
            var values = all.Select(app => ExportBytes(app.Metrics, first, second)).Where(value => value.HasValue).ToArray();
            return values.Length == 0 ? null : values.Sum(value => value!.Value);
        }
        var network = denominator(StatisticMetric.Download, StatisticMetric.Upload);
        var disk = denominator(StatisticMetric.DiskRead, StatisticMetric.DiskWrite);
        var applications = new List<StatisticsExportApplication>();
        foreach (var app in ordered.ThenBy(app => app.AppId, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            decimal? share(decimal? amount, decimal? total) => amount is not null && total > 0 ? amount / total : null;
            applications.Add(new(app.AppId, app.Software.DisplayName, app.Software.ProcessName, app.Software.Publisher,
                app.Software.Category, app.IsRunning, app.LastSeenUtc, app.ObservedSeconds, CopyExportMetrics(app.Metrics),
                share(ExportBytes(app.Metrics, StatisticMetric.Download, StatisticMetric.Upload), network),
                share(ExportBytes(app.Metrics, StatisticMetric.DiskRead, StatisticMetric.DiskWrite), disk)));
        }
        var scope = new StatisticsExportScope(request.HideSystem, search, request.View, request.Sort, request.Descending,
            all.Count, applications.Count, "all_attributable_applications", network, disk);
        var system = new StatisticsExportSystem(snapshot.System.ObservedSeconds, CopyExportMetrics(snapshot.System.Metrics),
            snapshot.TotalMemoryBytes, snapshot.DedicatedGpuCapacityBytes, snapshot.NetworkScope,
            snapshot.EtwQuality is { } quality ? quality with { } : null, snapshot.Warning);
        token.ThrowIfCancellationRequested();
        return new(1, request.RequestedAtUtc, context.Now, snapshot.StartUtc, context.Now, snapshot.LastRecordedUtc,
            snapshot.LastResetUtc, snapshot.LastSavedUtc, snapshot.SessionId, _timeZone.Id, request.Range,
            scope, system, applications.AsReadOnly());
    }

    private static decimal? ExportBytes(IReadOnlyDictionary<StatisticMetric, MetricStatistics> values, StatisticMetric a, StatisticMetric b) =>
        values.GetValueOrDefault(a)?.Bytes is { } av && values.GetValueOrDefault(b)?.Bytes is { } bv ? (decimal)av + bv : null;

    private static double? ExportSortValue(ApplicationStatistics app, string view, string column)
    {
        var m = app.Metrics;
        double? network() => (double?)ExportBytes(m, StatisticMetric.Download, StatisticMetric.Upload);
        double? disk() => (double?)ExportBytes(m, StatisticMetric.DiskRead, StatisticMetric.DiskWrite);
        return (view, column) switch
        {
            ("network", "second") => m.GetValueOrDefault(StatisticMetric.Download)?.Bytes,
            ("network", "third") => m.GetValueOrDefault(StatisticMetric.Upload)?.Bytes,
            ("network", _) => network(),
            ("disk", "second") => m.GetValueOrDefault(StatisticMetric.DiskRead)?.Bytes,
            ("disk", "third") => m.GetValueOrDefault(StatisticMetric.DiskWrite)?.Bytes,
            ("disk", _) => disk(),
            ("compute", "second") => m.GetValueOrDefault(StatisticMetric.Gpu)?.Average,
            ("compute", "third") => m.GetValueOrDefault(StatisticMetric.Memory)?.Peak,
            ("compute", "fourth") => m.GetValueOrDefault(StatisticMetric.DedicatedGpuMemory)?.Peak,
            ("overview", "second") => network(), ("overview", "third") => disk(),
            ("overview", "fourth") => m.GetValueOrDefault(StatisticMetric.Memory)?.Peak,
            _ => m.GetValueOrDefault(StatisticMetric.Cpu)?.CpuTicks
        };
    }

    private static IReadOnlyDictionary<StatisticMetric, StatisticsExportMetric> CopyExportMetrics(IReadOnlyDictionary<StatisticMetric, MetricStatistics> values) =>
        new ReadOnlyDictionary<StatisticMetric, StatisticsExportMetric>(StatisticMetrics.All.ToDictionary(key => key, key =>
        {
            var value = values.GetValueOrDefault(key) ?? new(null, 0, null, null, null, null, 0);
            var units = key switch
            {
                StatisticMetric.Cpu or StatisticMetric.Gpu => ("percent", "percent_seconds"),
                StatisticMetric.Download or StatisticMetric.Upload or StatisticMetric.DiskRead or StatisticMetric.DiskWrite => ("bytes_per_second", "bytes"),
                _ => ("bytes", "byte_seconds")
            };
            return new StatisticsExportMetric(units.Item1, units.Item2, value.Integral, value.ValidSeconds, value.Average,
                value.Peak, value.Bytes, value.CpuTicks, value.Coverage, value.PeakUtc);
        }));
}
