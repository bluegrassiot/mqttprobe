using MqttProbe.Services.Security;

namespace MqttProbe.Desktop.Services;

public class DesktopCertificateInputCapability : ICertificateInputCapability
{
    public bool UsesInputFileComponent => false;
}
