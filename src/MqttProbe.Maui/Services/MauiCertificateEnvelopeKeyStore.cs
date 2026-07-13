using MqttProbe.Services.Security;

namespace MqttProbe.Maui.Services;

public class MauiCertificateEnvelopeKeyStore : ICertificateEnvelopeKeyStore
{
    private readonly ISecretStorage _secretStorage;

    public MauiCertificateEnvelopeKeyStore(ISecretStorage secretStorage)
    {
        _secretStorage = secretStorage;
    }

    public Task<string?> GetAsync(string key) => _secretStorage.GetAsync(key);

    public Task SetAsync(string key, string value) => _secretStorage.SetAsync(key, value);

    public Task RemoveAsync(string key) => _secretStorage.RemoveAsync(key);
}
