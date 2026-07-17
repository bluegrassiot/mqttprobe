using MqttProbe.Desktop.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security.Fakes;

public sealed class FakeSecretKeyProtector : ISecretKeyProtector
{
    public SecretKeyLoadResult NextLoad { get; set; } = new SecretKeyLoadResult.NotFound();
    public SecretKeyStoreResult NextStore { get; set; } = new SecretKeyStoreResult.Stored();
    public byte[]? MemoryKey { get; set; }
    public int StoreCallCount { get; private set; }
    public int LoadCallCount { get; private set; }

    public Task<SecretKeyLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCallCount++;
        if (MemoryKey is { Length: MasterKeyConstants.KeySize })
            return Task.FromResult<SecretKeyLoadResult>(
                new SecretKeyLoadResult.Found((byte[])MemoryKey.Clone()));
        return Task.FromResult(NextLoad);
    }

    public Task<SecretKeyStoreResult> StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        StoreCallCount++;
        if (key.Length != MasterKeyConstants.KeySize)
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.UnexpectedFailure(
                    new SecretStorageException("bad length")));
        if (NextStore is SecretKeyStoreResult.Stored)
            MemoryKey = key.ToArray();
        return Task.FromResult(NextStore);
    }
}
