namespace MqttProbe.Desktop.Services.Security;

public sealed class MacKeychainSecretKeyProtector : ISecretKeyProtector
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecInteractionNotAllowed = -25308;
    private const int ErrSecNotAvailable = -25291;
    private const int ErrSecMissingEntitlement = -34018;

    private readonly IMacKeychainNative _native;
    private readonly bool _isMacOs;

    public MacKeychainSecretKeyProtector(IMacKeychainNative native, bool? isMacOs = null)
    {
        _native = native;
        _isMacOs = isMacOs ?? OperatingSystem.IsMacOS();
    }

    public Task<SecretKeyLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!_isMacOs)
            return Task.FromResult<SecretKeyLoadResult>(
                new SecretKeyLoadResult.FacilityUnavailable("Keychain is only available on macOS."));

        var status = _native.CopyMatching(
            MasterKeyConstants.AppServiceId, MasterKeyConstants.KeyVersionId, out var data);

        return Task.FromResult(MapLoadStatus(status, data));
    }

    public Task<SecretKeyStoreResult> StoreAsync(
        ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        if (!_isMacOs)
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.FacilityUnavailable("Keychain is only available on macOS."));

        if (key.Length != MasterKeyConstants.KeySize)
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.UnexpectedFailure(
                    new SecretStorageException("Master key must be 32 bytes.")));

        var status = _native.Update(
            MasterKeyConstants.AppServiceId, MasterKeyConstants.KeyVersionId, key.Span);

        if (status == ErrSecItemNotFound)
            status = _native.Add(
                MasterKeyConstants.AppServiceId, MasterKeyConstants.KeyVersionId, key.Span);

        return Task.FromResult(MapStoreStatus(status));
    }

    private static SecretKeyLoadResult MapLoadStatus(int status, byte[]? data)
    {
        if (status == ErrSecSuccess)
        {
            if (data is { Length: MasterKeyConstants.KeySize })
                return new SecretKeyLoadResult.Found(data);

            return new SecretKeyLoadResult.UnexpectedFailure(
                new SecretStorageException(
                    $"Keychain returned data with invalid length: {data?.Length ?? 0}."));
        }

        if (status == ErrSecItemNotFound)
            return new SecretKeyLoadResult.NotFound();

        if (status is ErrSecInteractionNotAllowed or ErrSecNotAvailable or ErrSecMissingEntitlement)
            return new SecretKeyLoadResult.FacilityUnavailable(
                $"Keychain facility unavailable (OSStatus {status}).");

        return new SecretKeyLoadResult.UnexpectedFailure(
            new SecretStorageException($"Keychain operation failed (OSStatus {status})."));
    }

    private static SecretKeyStoreResult MapStoreStatus(int status)
    {
        if (status == ErrSecSuccess)
            return new SecretKeyStoreResult.Stored();

        if (status is ErrSecInteractionNotAllowed or ErrSecNotAvailable or ErrSecMissingEntitlement)
            return new SecretKeyStoreResult.FacilityUnavailable(
                $"Keychain facility unavailable (OSStatus {status}).");

        return new SecretKeyStoreResult.UnexpectedFailure(
            new SecretStorageException($"Keychain operation failed (OSStatus {status})."));
    }
}
