namespace MqttProbe.Desktop.Services.Security;

public sealed class LinuxLibsecretKeyProtector : ISecretKeyProtector
{
    private const string SchemaName = "com.bluegrassiot.mqttprobe.MasterKey";
    private const string Label = "MQTTProbe master key";

    private readonly ILinuxLibsecretNative _native;
    private readonly bool _isLinux;

    public LinuxLibsecretKeyProtector(ILinuxLibsecretNative native, bool? isLinux = null)
    {
        _native = native;
        _isLinux = isLinux ?? OperatingSystem.IsLinux();
    }

    private static Dictionary<string, string> Attributes() =>
        new() { ["key-id"] = MasterKeyConstants.KeyVersionId };

    public Task<SecretKeyLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!_isLinux)
            return Task.FromResult<SecretKeyLoadResult>(
                new SecretKeyLoadResult.FacilityUnavailable("libsecret is only available on Linux."));

        var code = _native.Lookup(SchemaName, Attributes(), out var password, out var error);
        return Task.FromResult(MapLoad(code, password, error));
    }

    public Task<SecretKeyStoreResult> StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        if (!_isLinux)
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.FacilityUnavailable("libsecret is only available on Linux."));

        if (key.Length != MasterKeyConstants.KeySize)
            return Task.FromResult<SecretKeyStoreResult>(
                new SecretKeyStoreResult.UnexpectedFailure(
                    new SecretStorageException("Master key must be 32 bytes.")));

        var password = Convert.ToBase64String(key.Span);
        var code = _native.Store(SchemaName, Attributes(), Label, password, out var error);
        return Task.FromResult(MapStore(code, error));
    }

    private static SecretKeyLoadResult MapLoad(int code, string? password, string? error)
    {
        switch (code)
        {
            case 0:
                if (string.IsNullOrEmpty(password))
                    return new SecretKeyLoadResult.UnexpectedFailure(
                        new SecretStorageException("libsecret returned empty password."));
                try
                {
                    var bytes = Convert.FromBase64String(password);
                    if (bytes.Length != MasterKeyConstants.KeySize)
                        return new SecretKeyLoadResult.UnexpectedFailure(
                            new SecretStorageException("libsecret password decoded to invalid key length."));
                    return new SecretKeyLoadResult.Found(bytes);
                }
                catch (FormatException ex)
                {
                    return new SecretKeyLoadResult.UnexpectedFailure(ex);
                }
            case 1:
                return new SecretKeyLoadResult.NotFound();
            case 2:
                return new SecretKeyLoadResult.FacilityUnavailable(error);
            default:
                return new SecretKeyLoadResult.UnexpectedFailure(
                    new SecretStorageException(error ?? "libsecret lookup failed."));
        }
    }

    private static SecretKeyStoreResult MapStore(int code, string? error)
    {
        return code switch
        {
            0 => new SecretKeyStoreResult.Stored(),
            2 => new SecretKeyStoreResult.FacilityUnavailable(error),
            _ => new SecretKeyStoreResult.UnexpectedFailure(
                new SecretStorageException(error ?? "libsecret store failed.")),
        };
    }
}
