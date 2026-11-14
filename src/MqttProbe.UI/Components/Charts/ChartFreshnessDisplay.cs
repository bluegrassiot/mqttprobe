using MqttProbe.Core.Models.Chart;

namespace MqttProbe.UI.Components.Charts;

public static class ChartFreshnessDisplay
{
    public const string WaitingLabel = "Waiting for data";
    public const string PausedLabel = "Paused";

    public static string SummaryLabel(ChartFreshnessState state, bool paused) => paused
        ? PausedLabel
        : state switch
        {
            ChartFreshnessState.Waiting => WaitingLabel,
            ChartFreshnessState.Live or ChartFreshnessState.Disabled => "Live",
            ChartFreshnessState.Stale => "Stale",
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

    public static string TimeoutText(double seconds) => $"stale after {FormatSeconds(seconds)}";

    private static string FormatSeconds(double seconds) =>
        seconds < 1 ? $"{Math.Round(seconds, 1)}s" : $"{seconds:0.##}s";
}
