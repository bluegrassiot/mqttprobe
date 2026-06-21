using MQTTnet.Protocol;

namespace MqttProbe.Models.Mqtt;

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
}
