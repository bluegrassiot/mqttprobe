using MqttProbe.Services.Security;

namespace MqttProbe.Web.Services;

public class WebCertificateFilePicker : ICertificateFilePicker
{
    public Task<byte[]?> PickFileAsync(string title, string[] extensions, long maxBytes)
        => Task.FromResult<byte[]?>(null);
}
