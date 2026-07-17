using MqttProbe.Services.Security;

namespace MqttProbe.Web.Services;

public class WebCertificateInputCapability : ICertificateInputCapability
{
    public bool UsesInputFileComponent => true;
}
