namespace MqttProbe.Services.Mqtt;

public interface IConnectionSessionLifecycle
{
    public event Action? ActiveConnectionStopped;

    public Task StopActiveConnectionAsync();
}
