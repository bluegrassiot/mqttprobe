namespace MqttProbe.Core.Models.Chart;

public enum ChartFreshnessState
{
    Waiting,
    Live,
    Stale,
    Disabled
}

public readonly record struct ChartSeriesReceipt(Guid SeriesId, DateTime? LastReceivedUtc);

public sealed record ChartFreshnessReport(
    ChartFreshnessState State,
    DateTime? LastReceivedUtc,
    TimeSpan? Age);
