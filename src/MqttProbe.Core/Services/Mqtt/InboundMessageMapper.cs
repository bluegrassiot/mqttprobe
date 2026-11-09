using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal static class InboundMessageMapper
{
    public static MqttMessage Map(MqttApplicationMessage source, string payloadText, string topic,
        IReadOnlyDictionary<ulong, string>? aliasNames, string? formatId) =>
        new(payloadText, topic, source.Retain, source.QualityOfServiceLevel)
        {
            AliasNames = aliasNames,
            FormatId = formatId,
            ContentType = ContentType(source),
            ResponseTopic = ResponseTopic(source),
            CorrelationData = CorrelationData(source),
            MessageExpiryInterval = MessageExpiryInterval(source),
            PayloadFormatIndicator = PayloadFormatIndicator(source),
            UserProperties = UserProperties(source)
        };

    public static string? ContentType(MqttApplicationMessage message) => message.ContentType;

    public static string? ResponseTopic(MqttApplicationMessage message) => message.ResponseTopic;

    public static byte[]? CorrelationData(MqttApplicationMessage message) => message.CorrelationData;

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
}
