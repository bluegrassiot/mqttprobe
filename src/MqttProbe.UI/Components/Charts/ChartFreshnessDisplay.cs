using MqttProbe.Core.Models.Chart;

namespace MqttProbe.UI.Components.Charts;

public static class ChartFreshnessDisplay
{
    public const string WaitingLabel = "Waiting for data";
    public const string PausedLabel = "Paused";
    public const string DetectionOffLabel = "Freshness off";
    public const string SeriesNotCheckedLabel = "Not checked";

    public static string SummaryLabel(ChartFreshnessState state, bool paused) => paused
        ? PausedLabel
        : state switch
        {
            ChartFreshnessState.Waiting => WaitingLabel,
            ChartFreshnessState.Live => "Live",
            ChartFreshnessState.SomeStale => "Some series stale",
            ChartFreshnessState.Stale => "Stale",
            ChartFreshnessState.Mixed => "Mixed",
            ChartFreshnessState.Disabled => DetectionOffLabel,
            _ => WaitingLabel
        };

    public static string SeriesLabel(ChartSeriesFreshness series) => series.State switch
    {
        ChartSeriesFreshnessState.Fresh => "Live",
        ChartSeriesFreshnessState.Stale => "Stale",
        ChartSeriesFreshnessState.NotTracked => SeriesNotCheckedLabel,
        _ => WaitingLabel
    };

    public static string? AgeText(TimeSpan? age)
    {
        if (age is not { } value || value < TimeSpan.Zero)
            return null;

        if (value < TimeSpan.FromSeconds(1))
            return "just now";
        if (value < TimeSpan.FromMinutes(1))
            return $"{(int)value.TotalSeconds}s ago";
        if (value < TimeSpan.FromHours(1))
            return $"{(int)value.TotalMinutes}m {value.Seconds}s ago";
        if (value < TimeSpan.FromDays(1))
            return $"{(int)value.TotalHours}h {value.Minutes}m ago";
        return $"{(int)value.TotalDays}d ago";
    }

    public static bool ShowsCounts(ChartFreshnessReport report)
    {
        var buckets = 0;
        if (report.FreshSeriesCount > 0) buckets++;
        if (report.StaleSeriesCount > 0) buckets++;
        if (report.WaitingSeriesCount > 0) buckets++;
        if (report.NotTrackedSeriesCount > 0) buckets++;
        return buckets > 1;
    }

    public static string CountsText(ChartFreshnessReport report)
    {
        if (report.Series.Count == 0)
            return string.Empty;

        var parts = new List<string>();
        if (report.FreshSeriesCount > 0) parts.Add($"{report.FreshSeriesCount} live");
        if (report.StaleSeriesCount > 0) parts.Add($"{report.StaleSeriesCount} stale");
        if (report.WaitingSeriesCount > 0) parts.Add($"{report.WaitingSeriesCount} waiting");
        if (report.NotTrackedSeriesCount > 0) parts.Add($"{report.NotTrackedSeriesCount} not checked");
        return string.Join(" · ", parts);
    }

    public static string TimeoutText(double? seconds) =>
        seconds is { } value ? $"stale after {FormatSeconds(value)}" : DetectionOffLabel;

    private static string FormatSeconds(double seconds) =>
        seconds < 1 ? $"{Math.Round(seconds, 1)}s" : $"{seconds:0.##}s";
}
