using System.Security.Cryptography;
using System.Text;
using MqttProbe.Services.Security;

namespace MqttProbe.Services;

/// <summary>
/// Cross-platform secret storage. Each secret is encrypted at rest with AES-GCM using a
/// per-user random key persisted next to the secrets and restricted to the owning user on
/// Unix. This replaces the previous Windows-only DPAPI (<c>ProtectedData</c>) implementation,
/// which threw <see cref="PlatformNotSupportedException"/> on Linux/macOS.
/// </summary>
public class DesktopSecretStorage : ISecretStorage
{
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // AES-GCM standard nonce length
    private const int TagSize = 16;   // AES-GCM authentication tag length

    private readonly string _secretsDir;
    private readonly string _keyPath;
    private readonly object _keyLock = new();
    private byte[]? _key;

    public DesktopSecretStorage()
        : this(Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "mqttprobe", "secrets"))
    {
    }

    internal DesktopSecretStorage(string secretsDir)
    {
        _secretsDir = secretsDir;
        Directory.CreateDirectory(_secretsDir);
        _keyPath = Path.Combine(_secretsDir, ".key");
    }

    public Task<string?> GetAsync(string key)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        try
        {
            var plaintext = Decrypt(File.ReadAllBytes(path));
            return Task.FromResult<string?>(Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            // Fail soft: an unreadable, corrupt, or legacy (DPAPI-era) secret is treated as
            // "no secret" so the app prompts the user to re-enter it instead of crashing.
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            return RemoveAsync(key);

        var blob = Encrypt(Encoding.UTF8.GetBytes(value));
        var path = GetPath(key);
        File.WriteAllBytes(path, blob);
        RestrictToOwner(path);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        var path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(GetKey(), TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Layout: [nonce][tag][ciphertext]
        var blob = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, blob, NonceSize + TagSize, ciphertext.Length);
        return blob;
    }

    private byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Secret blob is too short to be valid.");

        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var ciphertext = blob.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(GetKey(), TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>Loads the per-user key, generating and persisting it on first use.</summary>
    private byte[] GetKey()
    {
        if (_key != null) return _key;
        lock (_keyLock)
        {
            if (_key != null) return _key;

            if (File.Exists(_keyPath))
            {
                var existing = File.ReadAllBytes(_keyPath);
                if (existing.Length == KeySize)
                    return _key = existing;
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllBytes(_keyPath, key);
            RestrictToOwner(_keyPath);
            return _key = key;
        }
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private string GetPath(string key)
    {
        var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .Replace('/', '_').Replace('+', '-').TrimEnd('=');
        return Path.Combine(_secretsDir, safe + ".dat");
    }
}
