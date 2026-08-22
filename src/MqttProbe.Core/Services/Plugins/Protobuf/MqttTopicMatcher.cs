using MQTTnet;

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
        if (string.IsNullOrWhiteSpace(filter) || filter.Contains('\0') || filter.Length > 65_535)
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
