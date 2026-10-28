using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.UI.Utilities;

namespace MqttProbe.UI.Components.Browser;

public sealed class PublishPropertiesDraft
{
    public const int MaxUserProperties = 50;

    public string? ContentType { get; set; }
    public MqttPayloadFormatIndicator PayloadFormat { get; set; } = MqttPayloadFormatIndicator.Unspecified;
    public uint? ExpirySeconds { get; set; }
    public string? ResponseTopic { get; set; }
    public string? CorrelationText { get; set; }
    public CorrelationDataEncoding CorrelationEncoding { get; set; } = CorrelationDataEncoding.Text;
    public IList<MqttMessageUserProperty> UserProperties { get; } = new List<MqttMessageUserProperty>();

    public int SetCount =>
        (string.IsNullOrEmpty(ContentType) ? 0 : 1) +
        (PayloadFormat == MqttPayloadFormatIndicator.Unspecified ? 0 : 1) +
        (ExpirySeconds is > 0 ? 1 : 0) +
        (string.IsNullOrEmpty(ResponseTopic) ? 0 : 1) +
        (string.IsNullOrEmpty(CorrelationText) ? 0 : 1) +
        UserProperties.Count;

    public bool Any => SetCount > 0;

    public bool CanAddUserProperty => UserProperties.Count < MaxUserProperties;

    public string? ResponseTopicError =>
        string.IsNullOrEmpty(ResponseTopic) || MqttTopicMatcher.IsValidTopicName(ResponseTopic)
            ? null
            : "Response topic must be a topic name without + or # wildcards.";

    public string? CorrelationError =>
        CorrelationDataCodec.TryParse(CorrelationText, CorrelationEncoding, out _)
            ? null
            : "Correlation data must be an even number of hex digits.";

    public string? UserPropertyError =>
        UserProperties.Count > MaxUserProperties
            ? $"At most {MaxUserProperties} user properties can be sent. Remove {UserProperties.Count - MaxUserProperties}."
            : UserProperties
                .Select((property, index) => string.IsNullOrWhiteSpace(property.Name) ? $"User property {index + 1} has a blank name. Remove it." : null)
                .FirstOrDefault(error => error is not null);

    public string? ValidationError => ResponseTopicError ?? CorrelationError ?? UserPropertyError;

    public static PublishPropertiesDraft From(MqttMessage message)
    {
        var (encoding, text) = CorrelationDataCodec.Format(message.CorrelationData);
        var draft = new PublishPropertiesDraft
        {
            ContentType = message.ContentType,
            PayloadFormat = message.PayloadFormatIndicator ?? MqttPayloadFormatIndicator.Unspecified,
            ExpirySeconds = message.MessageExpiryInterval is > 0 and var expiry ? expiry : null,
            ResponseTopic = message.ResponseTopic,
            CorrelationText = text,
            CorrelationEncoding = encoding
        };
        foreach (var property in message.UserProperties ?? [])
            draft.UserProperties.Add(property);
        return draft;
    }

    public MqttMessage ToMessage(MqttMessage source)
    {
        var correlation = CorrelationDataCodec.TryParse(CorrelationText, CorrelationEncoding, out var bytes) ? bytes : [];
        return new MqttMessage(source.Payload ?? string.Empty, source.Topic ?? string.Empty,
            source.RetainedMessage, source.QualityOfServiceLevel)
        {
            ContentType = string.IsNullOrEmpty(ContentType) ? null : ContentType,
            ResponseTopic = string.IsNullOrEmpty(ResponseTopic) ? null : ResponseTopic,
            CorrelationData = correlation.Length > 0 ? correlation : null,
            MessageExpiryInterval = ExpirySeconds is > 0 ? ExpirySeconds : null,
            PayloadFormatIndicator = PayloadFormat == MqttPayloadFormatIndicator.Unspecified ? null : PayloadFormat,
            UserProperties = UserProperties.Count > 0 ? UserProperties.ToArray() : null
        };
    }

    public void Clear()
    {
        ContentType = null;
        PayloadFormat = MqttPayloadFormatIndicator.Unspecified;
        ExpirySeconds = null;
        ResponseTopic = null;
        CorrelationText = null;
        CorrelationEncoding = CorrelationDataEncoding.Text;
        UserProperties.Clear();
    }
}
