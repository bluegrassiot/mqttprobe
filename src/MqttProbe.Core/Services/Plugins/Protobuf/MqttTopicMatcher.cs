using System.Text;
using MQTTnet;

namespace MqttProbe.Core.Services.Plugins.Protobuf;

public static class MqttTopicMatcher
{
    // MQTT caps topic names and filters at 65,535 bytes of UTF-8, not 65,535 characters.
    private const int MaxUtf8ByteLength = 65_535;

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
            Encoding.UTF8.GetByteCount(filter) > MaxUtf8ByteLength)
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
}
