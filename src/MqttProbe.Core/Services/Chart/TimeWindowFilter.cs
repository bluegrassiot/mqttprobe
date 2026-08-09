using MqttProbe.Core.Models.Chart;

namespace MqttProbe.Core.Services.Chart;

internal static class TimeWindowFilter
{
    internal static IReadOnlyList<ChartDataPoint> Apply(
        IReadOnlyList<ChartDataPoint> points,
        int? timeWindowMinutes,
        DateTime? now = null)
    {
        if (!timeWindowMinutes.HasValue)
            return [.. points];

        var cutoff = (now ?? DateTime.UtcNow).AddMinutes(-timeWindowMinutes.Value);
        return [.. points.Where(p => p.Timestamp >= cutoff)];
    }
}
