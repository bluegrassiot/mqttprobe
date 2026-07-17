namespace MqttProbe.Desktop.Services.Security;

public interface ISecretKeyProtector
{
    public Task<SecretKeyLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    public Task<SecretKeyStoreResult> StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);
}
