using System.Security.Cryptography.X509Certificates;

namespace MqttProbe.Services.Security;

public sealed class CertificateSessionResource : IDisposable
{
    private X509Certificate2? _certificate;
    private bool _disposed;

    public X509Certificate2? Certificate => _certificate;

    public void Set(X509Certificate2 cert)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_certificate is not null)
            throw new InvalidOperationException("Certificate already set; dispose before re-setting.");
        _certificate = cert;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _certificate?.Dispose();
        _certificate = null;
    }
}
