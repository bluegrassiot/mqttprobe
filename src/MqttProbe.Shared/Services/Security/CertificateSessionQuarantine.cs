using Microsoft.Extensions.Logging;

namespace MqttProbe.Services.Security;

public sealed class CertificateSessionQuarantine : ICertificateSessionQuarantine
{
    private readonly List<CertificateSessionResource> _quarantined = [];
    private readonly ILogger<CertificateSessionQuarantine> _logger;
    private readonly object _lock = new();

    public CertificateSessionQuarantine(ILogger<CertificateSessionQuarantine> logger)
    {
        _logger = logger;
    }

    public void Quarantine(CertificateSessionResource resource, string reason)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_lock)
        {
            _quarantined.Add(resource);
            _logger.LogWarning("Certificate session resource quarantined: {Reason}. " +
                "Resource will be released at process exit. Count: {Count}",
                reason, _quarantined.Count);
        }
    }
}
