namespace MqttProbe.PluginContracts;

public interface ITopologyExtractor
{
    public string FormatId { get; }
    public IReadOnlyList<TopologyEvent> Extract(DecodedPayloadEnvelope envelope);
}
