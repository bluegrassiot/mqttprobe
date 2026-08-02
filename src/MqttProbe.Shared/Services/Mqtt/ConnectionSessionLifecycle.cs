using Microsoft.Extensions.Logging;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Mqtt;

public sealed class ConnectionSessionLifecycle : IConnectionSessionLifecycle
{
    private readonly IMqttManagedClient _mqttClient;
    private readonly ISessionState _sessionState;
    private readonly ICertificateSessionQuarantine _quarantine;
    private readonly ILogger<ConnectionSessionLifecycle> _logger;

    public event Action? ActiveConnectionStopped;

    public ConnectionSessionLifecycle(
        IMqttManagedClient mqttClient,
        ISessionState sessionState,
        ICertificateSessionQuarantine quarantine,
        ILogger<ConnectionSessionLifecycle> logger)
    {
        _mqttClient = mqttClient;
        _sessionState = sessionState;
        _quarantine = quarantine;
        _logger = logger;
    }

    public async Task StopActiveConnectionAsync()
    {
        var certResource = _sessionState.ActiveCertificateResource;
        _sessionState.ActiveCertificateResource = null;

        try
        {
            await _mqttClient.StopAsync();
            certResource?.Dispose();
        }
        catch (Exception ex)
        {
            _sessionState.CertificateSessionFaulted = true;
            if (certResource is not null)
            {
                _quarantine.Quarantine(certResource,
                    $"StopActiveConnectionAsync StopAsync failed: {ex.Message}");
            }
            _logger.LogError(ex, "Failed to stop MQTT client cleanly; certificate quarantined");
            throw;
        }

        try
        {
            ActiveConnectionStopped?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ActiveConnectionStopped handler failed");
        }
    }
}
