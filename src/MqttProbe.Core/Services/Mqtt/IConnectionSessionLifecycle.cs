using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Services.Mqtt;

public interface IConnectionSessionLifecycle
{
    public event Action? ActiveConnectionStopped;

    public Task StopActiveConnectionAsync();

    public Task StopAttemptAsync(MqttConnectAttemptId attemptId, CertificateSessionResource? certificateResource);
}
