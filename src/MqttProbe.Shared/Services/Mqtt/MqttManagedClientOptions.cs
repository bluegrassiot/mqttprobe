using MQTTnet;

namespace MqttProbe.Services.Mqtt;

public sealed class MqttManagedClientOptions
{
    public required MqttClientOptions ClientOptions { get; init; }
    public TimeSpan AutoReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
}
