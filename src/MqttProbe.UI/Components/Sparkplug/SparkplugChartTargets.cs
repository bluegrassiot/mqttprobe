using System.Globalization;
using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Components.Sparkplug;

public sealed record SparkplugChartSelection(string Topic, string JsonPath, string SeriesName);

public static class SparkplugChartTargets
{
    public static string NodeTopic(string groupId, string nodeId) =>
        $"spBv1.0/{groupId}/NDATA/{nodeId}";

    public static string DeviceTopic(string groupId, string nodeId, string deviceId) =>
        $"spBv1.0/{groupId}/DDATA/{nodeId}/{deviceId}";

    public static string JsonPath(string metricName) => $"metrics.{metricName}";

    // Nodes charts metrics.<name>; string_value is not extracted into that stable-name path.
    public static bool IsChartable(SpbMetricSnapshot metric) =>
        metric.DataType switch
        {
            "datetime" => IsInvariantNumber(metric.Value),
            "bool" => metric.Value is "true" or "false",
            _ => SparkplugNodeDisplay.IsNumericType(metric.DataType) && IsInvariantNumber(metric.Value)
        };

    private static bool IsInvariantNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
