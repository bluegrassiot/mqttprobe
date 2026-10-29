using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

public static class OutboundMessageMapper
{
    public static MqttApplicationMessage Build(MqttMessage message)
    {
        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload)
            .WithQualityOfServiceLevel(message.QualityOfServiceLevel)
            .WithRetainFlag(message.RetainedMessage);

        if (!string.IsNullOrEmpty(message.ContentType))
            builder.WithContentType(message.ContentType);

        if (!string.IsNullOrEmpty(message.ResponseTopic))
            builder.WithResponseTopic(message.ResponseTopic);

        if (message.CorrelationData is { Length: > 0 } correlation)
            builder.WithCorrelationData(correlation);

        if (message.MessageExpiryInterval is > 0 and var expiry)
            builder.WithMessageExpiryInterval(expiry);

        if (message.PayloadFormatIndicator is { } indicator && indicator != MqttPayloadFormatIndicator.Unspecified)
            builder.WithPayloadFormatIndicator(indicator);

        if (message.UserProperties is { Count: > 0 } userProperties)
        {
            foreach (var property in userProperties)
                builder.WithUserProperty(property.Name, Encoding.UTF8.GetBytes(property.Value));
        }

        return builder.Build();
    }

    public static bool HasV5Properties(MqttMessage message) =>
        !string.IsNullOrEmpty(message.ContentType) ||
        !string.IsNullOrEmpty(message.ResponseTopic) ||
        message.CorrelationData is { Length: > 0 } ||
        message.MessageExpiryInterval is > 0 ||
        message.PayloadFormatIndicator is { } indicator && indicator != MqttPayloadFormatIndicator.Unspecified ||
        message.UserProperties is { Count: > 0 };
}
