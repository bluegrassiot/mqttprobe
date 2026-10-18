using Microsoft.Extensions.Logging;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Services.Mqtt;

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
        var certResource = _sessionState.TakeActiveCertificateResource();

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

    public async Task StopAttemptAsync(MqttConnectAttemptId attemptId, CertificateSessionResource? certificateResource)
    {
        CertificateSessionResource? claimed = null;
        long publicationVersion = 0;
        if (certificateResource is not null)
            claimed = _sessionState.TakeActiveCertificateResource(certificateResource, out publicationVersion);

        try
        {
            await _mqttClient.StopAttemptAsync(attemptId);
            claimed?.Dispose();
        }
        catch (Exception ex)
        {
            if (claimed is not null)
            {
                // Fault only the publication this claim observed: a replacement session that
                // published during the teardown owns the session now and must stay usable, while
                // the claimed resource is still this attempt's to quarantine.
                _sessionState.TryMarkCertificateSessionFaulted(publicationVersion);
                _quarantine.Quarantine(claimed,
                    $"StopAttemptAsync {attemptId.Value} failed: {ex.Message}");
            }
            _logger.LogError(ex, "Failed to stop connection attempt {AttemptId} cleanly", attemptId.Value);
            throw;
        }
    }
}
