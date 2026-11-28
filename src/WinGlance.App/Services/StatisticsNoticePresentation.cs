using WinGlance.Statistics;

namespace WinGlance.App.Services;

// Coverage is computed when a query completes; live state/progress are composed in constant time.
internal sealed record StatisticsNoticePresentation(string Message, bool IsWarning)
{
    internal static StatisticsNoticePresentation Create(string? coverage, bool coverageWarning,
        CollectionPresentation? collection, StatisticsRecordingProgress progress)
    {
        string? recording = progress.Phase switch
        {
            StatisticsRecordingPhase.WaitingForFirstInterval => "本次启动尚未入账，等待首个有效采集区间。",
            StatisticsRecordingPhase.WaitingAfterReset => "统计已重置，等待下一个有效采集区间。",
            StatisticsRecordingPhase.WaitingForBaseline => "采集基线正在预热，等待下一个有效区间后继续累计。",
            _ => null
        };
        if (progress.IsStalled)
            recording = Join(recording, "统计超过 6 秒未入账，累计值保留至最后一次有效区间。");
        if (collection is { IsLive: false })
            recording = Join("采集通道：" + collection.CompactStatus + "。", recording);
        else if (collection is { IsLive: true } && progress.IsStalled)
            recording = Join("采集通道实时，统计入账暂未推进。", recording);
        return new(Join(coverage, recording) ?? "", coverageWarning || progress.IsStalled);
    }

    private static string? Join(string? first, string? second) => string.IsNullOrEmpty(first) ? second :
        string.IsNullOrEmpty(second) ? first : first + "\n" + second;
}
