using System.Text.Json.Serialization;
using MQTTnet.Protocol;

namespace MqttProbe.Core.Models.Mqtt;

public class MqttMessage
{
    private Guid _id = Guid.NewGuid();
    private DateTime _dateTimeReceived = DateTime.UtcNow;

    public MqttMessage(string payload, string topic, bool retained, MqttQualityOfServiceLevel qos)
    {
        DateTimeReceived = DateTime.UtcNow;
        Payload = payload;
        Topic = topic;
        RetainedMessage = retained;
        QualityOfServiceLevel = qos;
    }

    public MqttMessage()
    {
    }

    public Guid Id
    {
        get => _id;
        init => _id = value == Guid.Empty ? Guid.NewGuid() : value;
    }

    public string? Payload { get; set; }
    public string? Topic { get; set; }
    public bool RetainedMessage { get; set; }
    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; set; }

    public DateTime DateTimeReceived
    {
        get => _dateTimeReceived;
        init => _dateTimeReceived = value == default ? DateTime.UtcNow : value;
    }

    [JsonIgnore]
    public TimeSpan? ElapsedSinceBufferStart { get; internal set; }

    [JsonIgnore]
    public TimeSpan? SincePreviousAllTopics { get; internal set; }

    [JsonIgnore]
    public TimeSpan? SincePreviousSameTopic { get; internal set; }

    public IReadOnlyDictionary<ulong, string>? AliasNames { get; init; }
    public string? FormatId { get; init; }

    public string? ContentType { get; init; }
    public string? ResponseTopic { get; init; }
    public byte[]? CorrelationData { get; init; }
    public uint? MessageExpiryInterval { get; init; }
    public MqttPayloadFormatIndicator? PayloadFormatIndicator { get; init; }
    public IReadOnlyList<MqttMessageUserProperty>? UserProperties { get; init; }
}
