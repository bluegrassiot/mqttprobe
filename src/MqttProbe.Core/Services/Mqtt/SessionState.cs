using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Services.Mqtt;

public interface ISessionState
{
    public event Action<Connection>? SelectedConnectionChanged;
    public Connection SelectedConnection { get; set; }
    public (Connection Connection, long Revision) CaptureSelectedConnection();
    public bool TryExecuteForSelectedConnection<T>(
        Guid connectionId, long revision, Func<T> mutation, out T result);
    public CertificateSessionResource? ActiveCertificateResource { get; set; }
    public CertificateSessionResource? TakeActiveCertificateResource();
    public CertificateSessionResource? TakeActiveCertificateResource(CertificateSessionResource expectedResource);
    public CertificateSessionResource? TakeActiveCertificateResource(
        CertificateSessionResource expectedResource, out long publicationVersion);
    public bool TryMarkCertificateSessionFaulted(long expectedPublicationVersion);
    public bool CertificateSessionFaulted { get; set; }
    public Guid? LastSuccessfulConnectionId { get; set; }
    public Connection? LastSuccessfulConnectionSnapshot { get; set; }
    public string? RetainedUnsavedAssetId { get; set; }
    public Guid RetainedUnsavedAssetOwnerId { get; set; }
}

public class SessionState : ISessionState
{
    // One lock for the certificate fields: every publication bumps the version, every take
    // detaches without bumping it, and a fault is only marked for the version it observed.
    private readonly Lock _certificateLock = new();
    private readonly Lock _selectionLock = new();
    private Connection _selectedConnection = new();
    private long _selectionRevision;
    private CertificateSessionResource? _activeCertificateResource;
    private long _certificatePublicationVersion;
    private bool _certificateSessionFaulted;

    public event Action<Connection>? SelectedConnectionChanged;

    public Connection SelectedConnection
    {
        get
        {
            lock (_selectionLock)
                return _selectedConnection;
        }
        set
        {
            lock (_selectionLock)
            {
                if (_selectedConnection.Equals(value))
                    return;

                _selectedConnection = value;
                _selectionRevision++;
            }

            SelectedConnectionChanged?.Invoke(value);
        }
    }

    public (Connection Connection, long Revision) CaptureSelectedConnection()
    {
        lock (_selectionLock)
            return (_selectedConnection, _selectionRevision);
    }

    public bool TryExecuteForSelectedConnection<T>(
        Guid connectionId, long revision, Func<T> mutation, out T result)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_selectionLock)
        {
            if (_selectedConnection.Id != connectionId || _selectionRevision != revision)
            {
                result = default!;
                return false;
            }

            result = mutation();
            return true;
        }
    }

    public CertificateSessionResource? ActiveCertificateResource
    {
        get
        {
            lock (_certificateLock)
                return _activeCertificateResource;
        }
        set
        {
            lock (_certificateLock)
            {
                _activeCertificateResource = value;
                _certificatePublicationVersion++;
            }
        }
    }

    public bool CertificateSessionFaulted
    {
        get
        {
            lock (_certificateLock)
                return _certificateSessionFaulted;
        }
        set
        {
            lock (_certificateLock)
                _certificateSessionFaulted = value;
        }
    }

    public CertificateSessionResource? TakeActiveCertificateResource()
    {
        lock (_certificateLock)
        {
            var taken = _activeCertificateResource;
            _activeCertificateResource = null;
            return taken;
        }
    }

    public CertificateSessionResource? TakeActiveCertificateResource(CertificateSessionResource expectedResource) =>
        TakeActiveCertificateResource(expectedResource, out _);

    // Identity take: only the expected resource is claimed, so a teardown aimed at a superseded
    // attempt can never take or release the replacement session's certificate. The captured
    // version identifies the publication this claim belongs to.
    public CertificateSessionResource? TakeActiveCertificateResource(
        CertificateSessionResource expectedResource, out long publicationVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedResource);

        lock (_certificateLock)
        {
            publicationVersion = _certificatePublicationVersion;
            if (!ReferenceEquals(_activeCertificateResource, expectedResource))
                return null;

            _activeCertificateResource = null;
            return expectedResource;
        }
    }

    // Rejects a fault whose publication has been superseded: a replacement session that published
    // after the claim must not be faulted by the older attempt's failed teardown.
    public bool TryMarkCertificateSessionFaulted(long expectedPublicationVersion)
    {
        lock (_certificateLock)
        {
            if (_certificatePublicationVersion != expectedPublicationVersion)
                return false;

            _certificateSessionFaulted = true;
            return true;
        }
    }

    public Guid? LastSuccessfulConnectionId { get; set; }
    public Connection? LastSuccessfulConnectionSnapshot { get; set; }
    public string? RetainedUnsavedAssetId { get; set; }
    public Guid RetainedUnsavedAssetOwnerId { get; set; }
}
