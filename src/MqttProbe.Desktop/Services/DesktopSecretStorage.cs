using System.Security.Cryptography;
using System.Text;
using MqttProbe.Desktop.Services.Security;
using MqttProbe.Services.Security;

namespace MqttProbe.Services;

public sealed class DesktopSecretStorage : ISecretStorage
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _secretsDir;
    private readonly DesktopSecretKeyProtector _keyProtector;

    public DesktopSecretStorage(string secretsDir, DesktopSecretKeyProtector keyProtector)
    {
        _secretsDir = secretsDir;
        _keyProtector = keyProtector;
        Directory.CreateDirectory(_secretsDir);
    }

    public async Task<string?> GetAsync(string key)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            var blob = await File.ReadAllBytesAsync(path);
            try
            {
                var plaintext = await DecryptAsync(blob);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (CryptographicException)
            {
                var migrated = TryMigrateLegacyDpapi(path, blob);
                if (migrated is not null)
                {
                    await SetAsync(key, migrated);
                    return migrated;
                }
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SetAsync(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            await RemoveAsync(key);
            return;
        }

        var blob = await EncryptAsync(Encoding.UTF8.GetBytes(value));
        var path = GetPath(key);
        await File.WriteAllBytesAsync(path, blob);
        RestrictToOwner(path);
    }

    public Task RemoveAsync(string key)
    {
        var path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private async Task<byte[]> EncryptAsync(byte[] plaintext)
    {
        var key = await GetKeyAsync();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var blob = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, blob, NonceSize + TagSize, ciphertext.Length);
        return blob;
    }

    private async Task<byte[]> DecryptAsync(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Secret blob is too short to be valid.");

        var key = await GetKeyAsync();
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var ciphertext = blob.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private async Task<byte[]> GetKeyAsync()
    {
        var key = await _keyProtector.GetOrCreateKeyAsync();
        if (key.Length != MasterKeyConstants.KeySize)
            throw new SecretStorageException("Master key length is invalid.");
        return key;
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string? TryMigrateLegacyDpapi(string path, byte[] blob)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var plain = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private string GetPath(string key)
    {
        var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .Replace('/', '_').Replace('+', '-').TrimEnd('=');
        return Path.Combine(_secretsDir, safe + ".dat");
    }
}
