using System.Security.Cryptography;

namespace MqttProbe.Desktop.Services.Security;

public sealed class WindowsDpapiSecretKeyProtector : ISecretKeyProtector
{
    private readonly string _blobPath;

    public WindowsDpapiSecretKeyProtector(string secretsDir)
    {
        _blobPath = Path.Combine(secretsDir, MasterKeyConstants.WindowsDpapiBlobFileName);
    }

    public Task<SecretKeyLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<SecretKeyLoadResult>(
                new SecretKeyLoadResult.FacilityUnavailable("DPAPI is only available on Windows."));

        try
        {
            if (!File.Exists(_blobPath))
                return Task.FromResult<SecretKeyLoadResult>(new SecretKeyLoadResult.NotFound());

            var protectedBytes = File.ReadAllBytes(_blobPath);
            var key = ProtectedData.Unprotect(
                protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);

            if (key.Length != MasterKeyConstants.KeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                return Task.FromResult<SecretKeyLoadResult>(
                    new SecretKeyLoadResult.UnexpectedFailure(
                        new SecretStorageException("DPAPI blob decrypted to invalid key length.")));
            }

            return Task.FromResult<SecretKeyLoadResult>(new SecretKeyLoadResult.Found(key));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Task.FromResult<SecretKeyLoadResult>(
                new SecretKeyLoadResult.UnexpectedFailure(ex));
        }
    }

    public Task<SecretKeyStoreResult> StoreAsync(
        ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.FacilityUnavailable("DPAPI is only available on Windows."));

        if (key.Length != MasterKeyConstants.KeySize)
        {
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.UnexpectedFailure(
                    new SecretStorageException("Master key must be 32 bytes.")));
        }

        try
        {
            var dir = Path.GetDirectoryName(_blobPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var protectedBytes = ProtectedData.Protect(
                key.ToArray(), optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_blobPath, protectedBytes);
            return Task.FromResult<SecretKeyStoreResult>(new SecretKeyStoreResult.Stored());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.UnexpectedFailure(ex));
        }
    }
}
