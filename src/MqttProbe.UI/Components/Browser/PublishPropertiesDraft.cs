using System.Globalization;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.UI.Utilities;

namespace MqttProbe.UI.Components.Browser;

public sealed class PublishPropertiesDraft
{
    public const int MaxUserProperties = 50;
    public const string ExpiryErrorText = "Message expiry must be a whole number of seconds from 0 to 4,294,967,295.";
    private const string Utf8Rule = "cannot contain NUL or exceed 65,535 bytes of UTF-8";

    public string? ContentType { get; set; }
    public MqttPayloadFormatIndicator PayloadFormat { get; set; } = MqttPayloadFormatIndicator.Unspecified;
    public string? ExpiryText { get; set; }

    public uint? ExpirySeconds
    {
        get => TryParseExpiry(ExpiryText, out var seconds) && seconds > 0 ? seconds : null;
        set => ExpiryText = value?.ToString(CultureInfo.InvariantCulture);
    }
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
        CorrelationDataCodec.TryParse(CorrelationText, CorrelationEncoding, out _, out var error) ? null : error;

    public string? ContentTypeError =>
        string.IsNullOrEmpty(ContentType) || MqttStringRules.IsValidUtf8String(ContentType)
            ? null
            : $"Content type {Utf8Rule}.";

    public string? ExpiryError =>
        string.IsNullOrWhiteSpace(ExpiryText) || TryParseExpiry(ExpiryText, out _) ? null : ExpiryErrorText;

    public string? UserPropertyError =>
        UserProperties.Count > MaxUserProperties
            ? $"At most {MaxUserProperties} user properties can be sent. Remove {UserProperties.Count - MaxUserProperties}."
            : UserProperties
                .Select((property, index) => DescribeUserProperty(index, property))
                .FirstOrDefault(error => error is not null);

    public string? ValidationError =>
        ResponseTopicError ?? CorrelationError ?? ContentTypeError ?? ExpiryError ?? UserPropertyError;

    public static string? DescribeUserProperty(int index, MqttMessageUserProperty property)
    {
        var position = index + 1;
        if (string.IsNullOrWhiteSpace(property.Name))
            return $"User property {position} has a blank name. Remove it.";
        if (!MqttStringRules.IsValidUtf8String(property.Name))
            return $"User property {position} name {Utf8Rule}. Remove it.";
        if (!MqttStringRules.IsValidUtf8String(property.Value))
            return $"User property {position} value {Utf8Rule}. Remove it.";
        return null;
    }

    private static bool TryParseExpiry(string? text, out uint seconds) =>
        uint.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds);

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
        ExpiryText = null;
        ResponseTopic = null;
        CorrelationText = null;
        CorrelationEncoding = CorrelationDataEncoding.Text;
        UserProperties.Clear();
    }
}
