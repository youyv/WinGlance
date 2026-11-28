using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinGlance.Core;

namespace WinGlance.Statistics;

public static class StatisticsExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task<StatisticsExportResult> WriteAsync(StatisticsExportOperation operation,
        StatisticsExportSnapshot snapshot, StatisticsExportDestination destination) =>
        WriteAsync(operation, snapshot, destination, ExportFileSystem.Instance);

    internal static Task<StatisticsExportResult> WriteAsync(StatisticsExportOperation operation,
        StatisticsExportSnapshot snapshot, StatisticsExportDestination destination, IExportFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        // This call registers Task.Run under the operation lock before returning to its caller.
        return operation.StartWriting(() => WriteCoreAsync(operation, snapshot, destination, files));
    }

    private static async Task<StatisticsExportResult> WriteCoreAsync(StatisticsExportOperation operation,
        StatisticsExportSnapshot snapshot, StatisticsExportDestination destination, IExportFileSystem files)
    {
        string? temporary = null;
        int protectedFields = 0;
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var created = CreateTemporary(Path.GetDirectoryName(destination.Path)!);
            temporary = created.Path; // Only a successfully created exclusive file is owned by this operation.
            await using (var stream = created.Stream)
            {
                if (operation.Format == StatisticsExportFormat.Json)
                    await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, operation.Token).ConfigureAwait(false);
                else protectedFields = await WriteCsvAsync(stream, snapshot, operation.Token).ConfigureAwait(false);
                await stream.FlushAsync(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
            if (!operation.TryBeginCommit()) return Cancel(temporary, files);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { return Cancel(temporary, files); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string? cleanup = StatisticsExportCommitter.CleanupOwnedTemporary(temporary, files);
            return new(StatisticsExportPhase.Failed, RecoveryPath: cleanup is null ? null : temporary,
                Error: "导出写入失败：" + error.Message, Warning: cleanup);
        }
        // No cancellation or unconditional cleanup is allowed beyond this commit boundary.
        var result = StatisticsExportCommitter.Commit(temporary!, destination, files);
        if (protectedFields > 0 && result.Phase == StatisticsExportPhase.Completed)
            result = result with { Warning = StatisticsExportCommitter.Join(result.Warning,
                $"CSV 中 {protectedFields} 个文本字段已添加“文本：”前缀，防止被表格软件识别为公式。") };
        return result;
    }

    private static StatisticsExportResult Cancel(string? temporary, IExportFileSystem files)
    {
        string? cleanup = StatisticsExportCommitter.CleanupOwnedTemporary(temporary, files);
        return new(StatisticsExportPhase.Cancelled, RecoveryPath: cleanup is null ? null : temporary, Warning: cleanup);
    }

    private static (FileStream Stream, string Path) CreateTemporary(string directory)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string path = Path.Combine(directory, ".winglance-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                return (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan), path);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 80 or 183) { }
        }
        throw new IOException("无法创建独占的导出临时文件。");
    }

    private static async Task<int> WriteCsvAsync(Stream stream, StatisticsExportSnapshot snapshot, CancellationToken token)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 65536, leaveOpen: true)
        { NewLine = "\r\n" };
        var header = new List<string>
        {
            "schema_version", "row_kind", "requested_at_utc", "as_of_utc", "start_utc", "end_utc", "range", "time_zone",
            "session_id", "last_recorded_utc", "last_reset_utc", "last_saved_utc", "hide_system", "search", "view", "sort", "descending",
            "all_applications_count", "exported_applications_count", "denominator_scope", "attributable_network_bytes", "attributable_disk_bytes",
            "app_id", "display_name", "process_name", "publisher", "category", "is_running", "last_seen_utc", "observed_seconds",
            "network_share", "disk_share", "total_memory_bytes", "dedicated_gpu_capacity_bytes", "network_scope", "warning",
            "csv_text_policy", "observation_basis", "cpu_tick_seconds", "etw_state", "etw_valid_seconds", "etw_lost_events",
            "etw_unattributed_events", "etw_unattributed_download_bytes", "etw_unattributed_upload_bytes", "etw_unattributed_read_bytes", "etw_unattributed_write_bytes"
        };
        string[] suffixes = ["value_unit", "integral_unit", "integral", "valid_seconds", "average", "peak", "bytes", "cpu_ticks", "coverage", "peak_utc"];
        foreach (var metric in StatisticMetrics.All) header.AddRange(suffixes.Select(suffix => metric + "_" + suffix));
        await writer.WriteLineAsync(string.Join(',', header).AsMemory(), token).ConfigureAwait(false);
        int protectedFields = 0;
        string text(string? value)
        {
            value ??= "";
            if (NeedsProtection(value)) { value = "文本：" + value; protectedFields++; }
            return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        }
        static string number<T>(T? value) where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture) ?? "";
        static string date(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
        async Task row(StatisticsExportApplication? app)
        {
            token.ThrowIfCancellationRequested();
            var scope = snapshot.Scope;
            var cells = new List<string>
            {
                snapshot.SchemaVersion.ToString(CultureInfo.InvariantCulture), app is null ? "system" : "application",
                date(snapshot.RequestedAtUtc), date(snapshot.AsOfUtc), date(snapshot.StartUtc), date(snapshot.EndUtc), snapshot.Range.ToString(), text(snapshot.TimeZoneId),
                snapshot.SessionId.ToString("D"), date(snapshot.LastRecordedUtc), date(snapshot.LastResetUtc), date(snapshot.LastSavedUtc),
                scope.HideSystem ? "true" : "false", text(scope.Search), text(scope.View), text(scope.Sort), scope.Descending ? "true" : "false",
                scope.AllApplicationsCount.ToString(CultureInfo.InvariantCulture), scope.ExportedApplicationsCount.ToString(CultureInfo.InvariantCulture),
                text(scope.DenominatorScope), number(scope.AttributableNetworkBytes), number(scope.AttributableDiskBytes),
                text(app?.AppId), text(app?.DisplayName), text(app?.ProcessName), text(app?.Publisher), text(app?.Category.ToString()),
                app is null ? "" : app.IsRunning ? "true" : "false", date(app?.LastSeenUtc), number<double>(app?.ObservedSeconds ?? snapshot.System.ObservedSeconds),
                number(app?.NetworkShare), number(app?.DiskShare), number(snapshot.System.TotalMemoryBytes), number(snapshot.System.DedicatedGpuCapacityBytes),
                text(snapshot.System.NetworkScope), text(snapshot.System.Warning), "visible_text_prefix_v1", snapshot.ObservationBasis, "0.0000001",
                text(snapshot.System.EtwQuality?.State.ToString()), number(snapshot.System.EtwQuality?.IntervalSeconds),
                number(snapshot.System.EtwQuality?.LostEvents), number(snapshot.System.EtwQuality?.UnattributedEvents),
                number(snapshot.System.EtwQuality?.UnattributedDownloadBytes), number(snapshot.System.EtwQuality?.UnattributedUploadBytes),
                number(snapshot.System.EtwQuality?.UnattributedDiskReadBytes), number(snapshot.System.EtwQuality?.UnattributedDiskWriteBytes)
            };
            var metrics = app?.Metrics ?? snapshot.System.Metrics;
            foreach (var key in StatisticMetrics.All)
            {
                var value = metrics.GetValueOrDefault(key);
                cells.AddRange([text(value?.ValueUnit), text(value?.IntegralUnit), number(value?.Integral), number(value?.ValidSeconds),
                    number(value?.Average), number(value?.Peak), number(value?.Bytes), number(value?.CpuTicks), number(value?.Coverage), date(value?.PeakUtc)]);
            }
            await writer.WriteLineAsync(string.Join(',', cells).AsMemory(), token).ConfigureAwait(false);
        }
        await row(null).ConfigureAwait(false);
        foreach (var application in snapshot.Applications) await row(application).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
        return protectedFields;
    }

    internal static bool NeedsProtection(string value) => value.Length != 0 &&
        (value[0] is '=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠' || char.IsControl(value[0]) ||
         char.IsWhiteSpace(value[0]) || char.GetUnicodeCategory(value, 0) == UnicodeCategory.Format);
}
