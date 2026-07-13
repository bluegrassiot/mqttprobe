namespace MqttProbe.Services.Mqtt;

public interface IConnectionSessionLifecycle
{
    public Task StopActiveConnectionAsync();
}
