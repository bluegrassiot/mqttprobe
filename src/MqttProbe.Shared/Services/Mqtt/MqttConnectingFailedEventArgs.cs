using MQTTnet;

namespace MqttProbe.Services.Mqtt;

public sealed class MqttConnectingFailedEventArgs : EventArgs
{
    public MqttConnectingFailedEventArgs(Exception? exception, MqttClientConnectResult? connectResult = null)
    {
        Exception = exception;
        ConnectResult = connectResult;
    }

    public Exception? Exception { get; }

    public MqttClientConnectResult? ConnectResult { get; }
}
