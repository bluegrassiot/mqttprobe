using MQTTnet;

namespace MqttProbe.Core.Services.Mqtt;

public readonly record struct MqttConnectAttemptId(long Value)
{
    public static MqttConnectAttemptId None => default;

    public bool IsNone => Value == 0L;
}

public enum MqttConnectStatus
{
    Connected,
    Failed,
    Canceled,
}

// A Canceled outcome never carries a failure and is never reported as a connect failure.
public sealed class MqttConnectOutcome
{
    public MqttConnectOutcome(
        MqttConnectAttemptId attemptId,
        MqttConnectStatus status,
        Exception? exception = null,
        MqttClientConnectResult? connectResult = null)
    {
        AttemptId = attemptId;
        Status = status;
        Exception = exception;
        ConnectResult = connectResult;
    }

    public MqttConnectAttemptId AttemptId { get; }

    public MqttConnectStatus Status { get; }

    public Exception? Exception { get; }

    public MqttClientConnectResult? ConnectResult { get; }
}

public sealed class MqttConnectAttempt
{
    internal MqttConnectAttempt(MqttConnectAttemptId id, Task<MqttConnectOutcome> completion)
    {
        Id = id;
        Completion = completion;
    }

    public MqttConnectAttemptId Id { get; }

    public Task<MqttConnectOutcome> Completion { get; }
}
