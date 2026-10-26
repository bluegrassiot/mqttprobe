using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal static class MqttV5PropertyReader
{
    public static string? ContentType(MqttApplicationMessage message) =>
        NullIfEmpty(message.ContentType);

    public static string? ResponseTopic(MqttApplicationMessage message) =>
        NullIfEmpty(message.ResponseTopic);

    public static byte[]? CorrelationData(MqttApplicationMessage message) =>
        message.CorrelationData is { Length: > 0 } data ? data : null;

    public static uint? MessageExpiryInterval(MqttApplicationMessage message) =>
        message.MessageExpiryInterval > 0 ? message.MessageExpiryInterval : null;

    public static MqttPayloadFormatIndicator? PayloadFormatIndicator(MqttApplicationMessage message) =>
        message.PayloadFormatIndicator == MqttPayloadFormatIndicator.Unspecified
            ? null
            : message.PayloadFormatIndicator;

    public static IReadOnlyList<MqttMessageUserProperty>? UserProperties(MqttApplicationMessage message)
    {
        if (message.UserProperties is not { Count: > 0 } source)
            return null;

        var result = new MqttMessageUserProperty[source.Count];
        for (var i = 0; i < source.Count; i++)
            result[i] = new MqttMessageUserProperty(source[i].Name, source[i].ReadValueAsString());
        return result;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
