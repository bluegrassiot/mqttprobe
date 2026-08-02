using MQTTnet;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Services.Plugins.Protobuf;

public sealed class ProtobufPayloadDetector : IPayloadDetector
{
    private const string SparkplugPrefix = "spBv1.0";

    private readonly ProtobufSchemaRegistry _registry;

    public ProtobufPayloadDetector(ProtobufSchemaRegistry registry) => _registry = registry;

    public string FormatId => "protobuf";

    public int Priority => 850;

    public bool CanDetect(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;

        if (topic.StartsWith(SparkplugPrefix, StringComparison.Ordinal))
            return false;

        return _registry.TryResolveByTopic(topic, out _);
    }
}
