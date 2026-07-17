using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Mqtt;

public interface ISessionState
{
    public event Action<Connection>? SelectedConnectionChanged;
    public Connection SelectedConnection { get; set; }
    public CertificateSessionResource? ActiveCertificateResource { get; set; }
    public bool CertificateSessionFaulted { get; set; }
}

public class SessionState : ISessionState
{
    private Connection _selectedConnection = new();

    public event Action<Connection>? SelectedConnectionChanged;

    public Connection SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (_selectedConnection.Equals(value))
                return;

            _selectedConnection = value;
            SelectedConnectionChanged?.Invoke(value);
        }
    }

    public CertificateSessionResource? ActiveCertificateResource { get; set; }
    public bool CertificateSessionFaulted { get; set; }
}
