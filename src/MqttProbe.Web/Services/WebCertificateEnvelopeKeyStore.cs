using MqttProbe.Services.Security;

namespace MqttProbe.Web.Services;

public class WebCertificateEnvelopeKeyStore : ICertificateEnvelopeKeyStore
{
    private readonly ISecretStorage _secretStorage;

    public WebCertificateEnvelopeKeyStore(ISecretStorage secretStorage)
    {
        _secretStorage = secretStorage;
    }

    public Task<string?> GetAsync(string key) => _secretStorage.GetAsync(key);

    public Task SetAsync(string key, string value) => _secretStorage.SetAsync(key, value);

    public Task RemoveAsync(string key) => _secretStorage.RemoveAsync(key);
}
