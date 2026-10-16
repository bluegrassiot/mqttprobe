using MQTTnet;

namespace MqttProbe.Core.Services.Mqtt;

// A fresh client per explicit Start keeps a retired attempt's callbacks off the next attempt's client.
public interface IMqttClientFactory
{
    public IMqttClient CreateClient();

    public bool OwnsClients { get; }
}

internal sealed class MqttNetClientFactory : IMqttClientFactory
{
    public IMqttClient CreateClient() => new MqttClientFactory().CreateMqttClient();

    public bool OwnsClients => true;
}

// One instance backs every generation, so the manager never disposes it.
internal sealed class AdoptedClientFactory(IMqttClient client) : IMqttClientFactory
{
    public IMqttClient CreateClient() => client;

    public bool OwnsClients => false;
}
