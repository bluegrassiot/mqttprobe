namespace MqttProbe.Services.Plugins.Contracts;

public interface ITopologyExtractor
{
    public string FormatId { get; }
    public IReadOnlyList<TopologyEvent> Extract(DecodedPayloadEnvelope envelope);
}
