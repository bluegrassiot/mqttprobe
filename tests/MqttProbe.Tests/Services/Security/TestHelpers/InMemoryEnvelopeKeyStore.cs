using MqttProbe.Services.Security;

namespace MqttProbe.Tests.Services.Security.TestHelpers;

internal class InMemoryEnvelopeKeyStore : ICertificateEnvelopeKeyStore
{
    private readonly Dictionary<string, string> _store = new();
    public bool IsEmpty => _store.Count == 0;
    public Task<string?> GetAsync(string key) => Task.FromResult(_store.GetValueOrDefault(key));
    public Task SetAsync(string key, string value) { _store[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key) { _store.Remove(key); return Task.CompletedTask; }
}
