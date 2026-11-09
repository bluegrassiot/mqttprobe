using MqttProbe.Core.Models.Chart;

namespace MqttProbe.Core.Services.Chart;

public static class ChartFreshnessEvaluator
{
    public static bool IsValidTimeout(double? seconds) =>
        seconds is null || (double.IsFinite(seconds.Value) && seconds.Value > 0);

    public static ChartFreshnessReport Evaluate(
        IReadOnlyList<ChartSeriesReceipt> receipts,
        double? staleTimeoutSeconds,
        DateTime nowUtc)
    {
        var timeoutSeconds = staleTimeoutSeconds is { } seconds && IsValidTimeout(seconds)
            ? seconds
            : (double?)null;

        var series = receipts
            .Select(receipt => EvaluateSeries(receipt, timeoutSeconds, nowUtc))
            .ToList();

        var waiting = series.Count(s => s.State == ChartSeriesFreshnessState.Waiting);
        var stale = series.Count(s => s.State == ChartSeriesFreshnessState.Stale);
        var notTracked = series.Count(s => s.State == ChartSeriesFreshnessState.NotTracked);

        ChartFreshnessState state;
        if (waiting == series.Count)
            state = ChartFreshnessState.Waiting;
        else if (timeoutSeconds is null)
            state = ChartFreshnessState.Disabled;
        else if (waiting > 0)
            state = ChartFreshnessState.Mixed;
        else if (stale == 0)
            state = ChartFreshnessState.Live;
        else if (stale == series.Count)
            state = ChartFreshnessState.Stale;
        else
            state = ChartFreshnessState.SomeStale;

        return new ChartFreshnessReport(
            state,
            series,
            series.Count - waiting - stale - notTracked,
            stale,
            waiting,
            notTracked);
    }

    private static ChartSeriesFreshness EvaluateSeries(
        ChartSeriesReceipt receipt,
        double? timeoutSeconds,
        DateTime nowUtc)
    {
        if (receipt.LastReceivedUtc is not { } lastReceived)
        {
            return new ChartSeriesFreshness(
                receipt.SeriesId,
                ChartSeriesFreshnessState.Waiting,
                null,
                null);
        }

        var age = nowUtc - lastReceived;

        if (timeoutSeconds is not { } timeout)
        {
            return new ChartSeriesFreshness(
                receipt.SeriesId,
                ChartSeriesFreshnessState.NotTracked,
                lastReceived,
                age);
        }

        return new ChartSeriesFreshness(
            receipt.SeriesId,
            age.TotalSeconds > timeout ? ChartSeriesFreshnessState.Stale : ChartSeriesFreshnessState.Fresh,
            lastReceived,
            age);
    }
}
