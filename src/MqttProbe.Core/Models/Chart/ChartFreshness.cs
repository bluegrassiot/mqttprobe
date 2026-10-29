namespace MqttProbe.Core.Models.Chart;

public enum ChartSeriesFreshnessState
{
    Waiting,
    Fresh,
    Stale,
    NotTracked
}

public enum ChartFreshnessState
{
    Waiting,
    Live,
    SomeStale,
    Stale,
    Mixed,
    Disabled
}

public readonly record struct ChartSeriesReceipt(Guid SeriesId, DateTime? LastReceivedUtc);

public sealed record ChartSeriesFreshness(
    Guid SeriesId,
    ChartSeriesFreshnessState State,
    DateTime? LastReceivedUtc,
    TimeSpan? Age);

public sealed record ChartFreshnessReport(
    ChartFreshnessState State,
    IReadOnlyList<ChartSeriesFreshness> Series,
    int FreshSeriesCount,
    int StaleSeriesCount,
    int WaitingSeriesCount,
    int NotTrackedSeriesCount);
