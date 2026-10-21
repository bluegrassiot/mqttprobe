using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Components.Sparkplug;

public static class SparkplugChartField
{
    public static string NodeTopic(string groupId, string nodeId) =>
        $"spBv1.0/{groupId}/NDATA/{nodeId}";

    public static string DeviceTopic(string groupId, string nodeId, string deviceId) =>
        $"spBv1.0/{groupId}/DDATA/{nodeId}/{deviceId}";

    // The chart collector names Sparkplug metrics after the metric name, so the path
    // keeps the name verbatim, spaces and case included.
    public static string JsonPath(string metricName) => $"metrics.{metricName}";

    // Numeric and boolean datatypes are chartable. A Sparkplug string metric travels as
    // stringValue, which the collector does not read, so offering it would create an
    // empty series. 64-bit integers travel as numeric strings and are collected, which
    // is why every numeric datatype stays eligible.
    public static bool IsChartable(SpbMetricSnapshot metric) =>
        SparkplugNodeDisplay.IsNumericType(metric.DataType)
        || IsBooleanType(metric.DataType);

    private static bool IsBooleanType(string dataType) =>
        dataType is "bool" or "boolean";
}
