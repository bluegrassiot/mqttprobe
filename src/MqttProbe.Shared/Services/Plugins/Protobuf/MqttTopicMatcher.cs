using MQTTnet;

namespace MqttProbe.Services.Plugins.Protobuf;

public static class MqttTopicMatcher
{
    public static bool Matches(string topic, string filter) =>
        MqttTopicFilterComparer.Compare(topic, filter) == MqttTopicFilterCompareResult.IsMatch;
}
