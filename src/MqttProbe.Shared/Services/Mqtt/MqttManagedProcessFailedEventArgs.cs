namespace MqttProbe.Services.Mqtt;

public sealed class MqttManagedProcessFailedEventArgs : EventArgs
{
    public MqttManagedProcessFailedEventArgs(Exception exception)
    {
        Exception = exception;
    }

    public Exception Exception { get; }
}
