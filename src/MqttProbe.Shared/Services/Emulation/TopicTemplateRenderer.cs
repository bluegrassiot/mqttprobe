using MqttProbe.Models.Emulation;

namespace MqttProbe.Services.Emulation;

public static class TopicTemplateRenderer
{
    public const string GroupToken = "{group}";
    public const string NodeToken = "{node}";
    public const string DeviceToken = "{device}";
    public const string MetricToken = "{metric}";

    private static readonly char[] _illegalSegmentChars = ['/', '+', '#'];

    public static string RenderMetricTopic(EmulatorNodeConfig node, string deviceId, string metricName) =>
        node.TopicTemplate
            .Replace(GroupToken, node.GroupId)
            .Replace(NodeToken, node.NodeId)
            .Replace(DeviceToken, deviceId)
            .Replace(MetricToken, metricName);

    public static string RenderDeviceTopic(EmulatorNodeConfig node, string deviceId)
    {
        // Json payloads bundle all metrics per device, so the {metric} segment has no value to carry.
        var segments = node.TopicTemplate
            .Split('/')
            .Where(s => !s.Contains(MetricToken, StringComparison.Ordinal));
        return string.Join('/', segments)
            .Replace(GroupToken, node.GroupId)
            .Replace(NodeToken, node.NodeId)
            .Replace(DeviceToken, deviceId);
    }

    public static IReadOnlyList<string> Validate(EmulatorNodeConfig node)
    {
        if (node.Type != EmulatorNodeType.Generic) return [];

        var errors = new List<string>();
        var template = node.TopicTemplate;

        if (!template.Contains(NodeToken, StringComparison.Ordinal))
            errors.Add($"Topic template must contain {NodeToken}.");
        if (node.PayloadFormat is GenericPayloadFormat.PlainText or GenericPayloadFormat.Hex
            && !template.Contains(MetricToken, StringComparison.Ordinal))
            errors.Add($"Topic template must contain {MetricToken} for per-metric payload formats.");
        if (node.Devices.Count > 0 && !template.Contains(DeviceToken, StringComparison.Ordinal))
            errors.Add($"Topic template must contain {DeviceToken} when the node has devices.");

        AddSegmentErrors(errors, node.GroupId, "Group ID");
        AddSegmentErrors(errors, node.NodeId, "Node ID");
        foreach (var device in node.Devices)
        {
            AddSegmentErrors(errors, device.DeviceId, "Device ID");
            foreach (var metric in device.Metrics)
                AddSegmentErrors(errors, metric.Name, "Metric name");
        }

        return errors;
    }

    private static void AddSegmentErrors(List<string> errors, string value, string fieldName)
    {
        if (value.IndexOfAny(_illegalSegmentChars) >= 0)
            errors.Add($"{fieldName} \"{value}\" contains characters not allowed in topic segments (/ + #).");
    }
}
