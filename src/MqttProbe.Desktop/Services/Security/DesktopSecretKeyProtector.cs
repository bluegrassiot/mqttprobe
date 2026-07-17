using System.Security.Cryptography;
using MqttProbe.Services.Security;

namespace MqttProbe.Desktop.Services.Security;

public sealed class DesktopSecretKeyProtector : ISecretProtectionStatus
{
    private readonly string _secretsDir;
    private readonly ISecretKeyProtector _os;
    private readonly ISecretKeyProtector _file;
    private readonly IRawSecretKeyFile _raw;
    private readonly Func<bool> _hasCiphertextFiles;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    private SecretProtectionMode? _mode;
    private byte[]? _cachedKey;
    private bool _initialized;

    public DesktopSecretKeyProtector(
        string secretsDir,
        ISecretKeyProtector osBackend,
        ISecretKeyProtector fileBackend,
        IRawSecretKeyFile rawKeyFile,
        Func<bool>? hasCiphertextFiles = null)
    {
        _secretsDir = secretsDir;
        _os = osBackend;
        _file = fileBackend;
        _raw = rawKeyFile;
        _hasCiphertextFiles = hasCiphertextFiles
            ?? (() => Directory.Exists(secretsDir)
                     && Directory.EnumerateFiles(secretsDir, "*.dat").Any());
    }

    public SecretProtectionMode Mode =>
        _mode ?? throw new InvalidOperationException(
            "Secret key protection has not been initialized.");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_secretsDir);

        var osLoad = await _os.LoadAsync(cancellationToken);

        switch (osLoad)
        {
            case SecretKeyLoadResult.UnexpectedFailure unexpected:
                throw new SecretKeyFacilityException(
                    "OS keyring facility failed during initialization.", unexpected.Error);

            case SecretKeyLoadResult.FacilityUnavailable:
                await InitializeFileFallbackAsync(cancellationToken);
                return;

            case SecretKeyLoadResult.Found found:
                await InitializeWithOsKeyAsync(found.Key, cancellationToken);
                return;

            case SecretKeyLoadResult.NotFound:
                await InitializeWithNoOsKeyAsync(cancellationToken);
                return;
        }
    }

    public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            throw new InvalidOperationException(
                "Secret key protection has not been initialized.");

        if (_cachedKey is not null)
            return (byte[])_cachedKey.Clone();

        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedKey is not null)
                return (byte[])_cachedKey.Clone();

            var backend = _mode == SecretProtectionMode.FileFallback ? _file : _os;
            var loadResult = await backend.LoadAsync(cancellationToken);

            switch (loadResult)
            {
                case SecretKeyLoadResult.Found found:
                    _cachedKey = found.Key;
                    return (byte[])_cachedKey.Clone();

                case SecretKeyLoadResult.NotFound:
                    return await CreateAndConfirmAsync(backend, cancellationToken);

                case SecretKeyLoadResult.FacilityUnavailable:
                    throw new SecretKeyFacilityException(
                        "Secret key facility is unavailable or failed.");

                case SecretKeyLoadResult.UnexpectedFailure unexpected:
                    throw new SecretKeyFacilityException(
                        "Secret key facility is unavailable or failed.", unexpected.Error);
            }
        }
        finally
        {
            _keyLock.Release();
        }

        throw new InvalidOperationException("Unreachable");
    }

    private Task InitializeFileFallbackAsync(CancellationToken cancellationToken)
    {
        byte[]? rawKey;
        try
        {
            rawKey = _raw.TryRead();
        }
        catch (SecretStorageException)
        {
            throw;
        }

        if (rawKey is not null)
        {
            _mode = SecretProtectionMode.FileFallback;
            _cachedKey = rawKey;
            _initialized = true;
            return Task.CompletedTask;
        }

        if (!_hasCiphertextFiles())
        {
            _mode = SecretProtectionMode.FileFallback;
            _initialized = true;
            return Task.CompletedTask;
        }

        if (HasRecoverableLegacySecrets())
        {
            _mode = SecretProtectionMode.FileFallback;
            _initialized = true;
            return Task.CompletedTask;
        }

        throw new OrphanedSecretStoreException(
            "Ciphertext files exist but no raw key file is present and OS keyring is unavailable.");
    }

    private Task InitializeWithOsKeyAsync(byte[] osKey, CancellationToken cancellationToken)
    {
        if (_raw.Exists)
        {
            byte[]? rawKey;
            try
            {
                rawKey = _raw.TryRead();
            }
            catch (SecretStorageException)
            {
                throw;
            }

            if (rawKey is not null && CryptographicOperations.FixedTimeEquals(osKey, rawKey))
            {
                if (!_raw.TryDelete())
                    throw new PartialSecretKeyMigrationException(
                        "Failed to delete raw key file after confirming it matches the OS keyring key.");

                _mode = SecretProtectionMode.OsKeyring;
                _cachedKey = osKey;
                _initialized = true;
                return Task.CompletedTask;
            }

            if (_hasCiphertextFiles())
                throw new AmbiguousSecretKeyException(
                    "OS keyring and raw key file contain different keys, and ciphertext files exist.");

            if (!_raw.TryDelete())
                throw new PartialSecretKeyMigrationException(
                    "Failed to delete stale raw key file that differs from OS keyring key.");

            _mode = SecretProtectionMode.OsKeyring;
            _cachedKey = osKey;
            _initialized = true;
            return Task.CompletedTask;
        }

        _mode = SecretProtectionMode.OsKeyring;
        _cachedKey = osKey;
        _initialized = true;
        return Task.CompletedTask;
    }

    private async Task InitializeWithNoOsKeyAsync(CancellationToken cancellationToken)
    {
        if (_raw.Exists)
        {
            byte[]? rawKey;
            try
            {
                rawKey = _raw.TryRead();
            }
            catch (SecretStorageException)
            {
                throw;
            }

            if (rawKey is not null)
            {
                var storeResult = await _os.StoreAsync(rawKey, cancellationToken);

                if (storeResult is SecretKeyStoreResult.FacilityUnavailable)
                {
                    _mode = SecretProtectionMode.FileFallback;
                    _cachedKey = rawKey;
                    _initialized = true;
                    return;
                }

                if (storeResult is SecretKeyStoreResult.UnexpectedFailure unexpected)
                    throw new SecretKeyFacilityException(
                        "Failed to migrate raw key to OS keyring.", unexpected.Error);

                var confirm = await _os.LoadAsync(cancellationToken);
                if (confirm is not SecretKeyLoadResult.Found found ||
                    !CryptographicOperations.FixedTimeEquals(rawKey, found.Key))
                    throw new SecretKeyFacilityException(
                        "OS keyring store succeeded but the stored key could not be confirmed.");

                if (!_raw.TryDelete())
                    throw new PartialSecretKeyMigrationException(
                        "Failed to delete raw key file after migrating to OS keyring.");

                _mode = SecretProtectionMode.OsKeyring;
                _cachedKey = rawKey;
                _initialized = true;
                return;
            }
        }

        if (_hasCiphertextFiles())
        {
            if (HasRecoverableLegacySecrets())
            {
                _mode = SecretProtectionMode.OsKeyring;
                _initialized = true;
                return;
            }

            throw new OrphanedSecretStoreException(
                "Ciphertext files exist but no key is available in OS keyring or raw key file.");
        }

        _mode = SecretProtectionMode.OsKeyring;
        _initialized = true;
    }

    private bool HasRecoverableLegacySecrets()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (!Directory.Exists(_secretsDir))
            return false;

        var files = Directory.EnumerateFiles(_secretsDir, "*.dat").ToList();
        if (files.Count == 0)
            return false;

        foreach (var path in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var plain = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                CryptographicOperations.ZeroMemory(plain);
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private async Task<byte[]> CreateAndConfirmAsync(
        ISecretKeyProtector backend, CancellationToken cancellationToken)
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);

        var storeResult = await backend.StoreAsync(key, cancellationToken);

        if (storeResult is SecretKeyStoreResult.FacilityUnavailable or
            SecretKeyStoreResult.UnexpectedFailure)
            throw new SecretKeyFacilityException(
                "Failed to store newly created secret key.");

        var confirm = await backend.LoadAsync(cancellationToken);
        if (confirm is not SecretKeyLoadResult.Found found ||
            !CryptographicOperations.FixedTimeEquals(key, found.Key))
            throw new SecretKeyFacilityException(
                "Secret key was stored but could not be confirmed on read-back.");

        _cachedKey = found.Key;
        return (byte[])_cachedKey.Clone();
    }
}
