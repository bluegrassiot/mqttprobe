using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Components.Sparkplug;

public sealed record SparkplugMetricWriteTarget(
    string GroupId, string NodeId, string? DeviceId, SpbMetricSnapshot Metric)
{
    public string CommandTopic => DeviceId is null
        ? $"spBv1.0/{GroupId}/NCMD/{NodeId}"
        : $"spBv1.0/{GroupId}/DCMD/{NodeId}/{DeviceId}";

    public string Path => DeviceId is null ? $"{GroupId}/{NodeId}" : $"{GroupId}/{NodeId}/{DeviceId}";
}
