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
        if (staleTimeoutSeconds is not { } timeout || !IsValidTimeout(timeout))
            return new ChartFreshnessReport(ChartFreshnessState.Disabled, null, null);

        DateTime? newest = null;
        foreach (var receipt in receipts)
        {
            if (receipt.LastReceivedUtc is { } received
                && (newest is null || received > newest))
                newest = received;
        }

        if (newest is not { } lastReceived)
            return new ChartFreshnessReport(ChartFreshnessState.Waiting, null, null);

        var age = nowUtc - lastReceived;
        var state = age.TotalSeconds > timeout
            ? ChartFreshnessState.Stale
            : ChartFreshnessState.Live;

        return new ChartFreshnessReport(state, lastReceived, age);
    }
}
