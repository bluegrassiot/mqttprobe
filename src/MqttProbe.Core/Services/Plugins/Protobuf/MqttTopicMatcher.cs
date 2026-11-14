using System.Text;
using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Services.Plugins.Protobuf;

public static class MqttTopicMatcher
{
    public static bool Matches(string topic, string filter)
    {
        if (!IsValidFilter(filter))
            return false;

        return MqttTopicFilterComparer.Compare(topic, filter) == MqttTopicFilterCompareResult.IsMatch;
    }

    public static bool IsValidFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) ||
            filter.Contains('\0') ||
            Encoding.UTF8.GetByteCount(filter) > MqttStringRules.MaxUtf8Bytes)
            return false;

        var levels = filter.Split('/');
        for (var i = 0; i < levels.Length; i++)
        {
            var level = levels[i];
            if (level.Contains('#') && (level != "#" || i != levels.Length - 1))
                return false;
            if (level.Contains('+') && level != "+")
                return false;
        }

        return true;
    }

    public static bool IsValidTopicName(string? topic) =>
        !string.IsNullOrEmpty(topic) &&
        !topic.Contains('\0') &&
        !topic.Contains('#') &&
        !topic.Contains('+') &&
        Encoding.UTF8.GetByteCount(topic) <= MqttStringRules.MaxUtf8Bytes;
}
