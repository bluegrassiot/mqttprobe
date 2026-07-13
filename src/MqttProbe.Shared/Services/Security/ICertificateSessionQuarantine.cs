namespace MqttProbe.Services.Security;

public interface ICertificateSessionQuarantine
{
    public void Quarantine(CertificateSessionResource resource, string reason);
}
