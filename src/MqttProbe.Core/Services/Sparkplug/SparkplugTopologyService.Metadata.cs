namespace MqttProbe.Core.Services.Sparkplug;

public sealed partial class SparkplugTopologyService
{
    public uint? ResolveAdvertisedDatatype(string groupId, string nodeId, string? deviceId, string metricName) =>
        _metricMetadata.ResolveDatatype(groupId, nodeId, deviceId, 0, metricName);
}
