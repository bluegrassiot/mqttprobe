namespace MqttProbe.Desktop.Services.Security;

public sealed class FileSecretKeyProtector : ISecretKeyProtector
{
    private readonly IRawSecretKeyFile _raw;

    public FileSecretKeyProtector(IRawSecretKeyFile raw) => _raw = raw;

    public Task<SecretKeyLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var key = _raw.TryRead();
            if (key is null)
            {
                return Task.FromResult<SecretKeyLoadResult>(new SecretKeyLoadResult.NotFound());
            }

            return Task.FromResult<SecretKeyLoadResult>(new SecretKeyLoadResult.Found(key));
        }
        catch (Exception ex)
        {
            return Task.FromResult<SecretKeyLoadResult>(new SecretKeyLoadResult.UnexpectedFailure(ex));
        }
    }

    public Task<SecretKeyStoreResult> StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        if (key.Length != MasterKeyConstants.KeySize)
        {
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.UnexpectedFailure(
                    new SecretStorageException("Master key must be 32 bytes.")));
        }

        try
        {
            _raw.Write(key.Span);
            return Task.FromResult<SecretKeyStoreResult>(new SecretKeyStoreResult.Stored());
        }
        catch (Exception ex)
        {
            return Task.FromResult<SecretKeyStoreResult>(new SecretKeyStoreResult.UnexpectedFailure(ex));
        }
    }
}
