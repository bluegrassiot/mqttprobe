using MqttProbe.Services.Security;

namespace MqttProbe.Tests.Services.Security.TestHelpers;

internal class FailingEnvelopeKeyStore : ICertificateEnvelopeKeyStore
{
    public Task<string?> GetAsync(string key) => throw new IOException("disk full");
    public Task SetAsync(string key, string value) => throw new IOException("disk full");
    public Task RemoveAsync(string key) => throw new IOException("disk full");
}
