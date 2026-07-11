using System.Security.Cryptography;
using System.Text;
using MqttProbe.Services.Security;

#pragma warning disable CA1416

namespace MqttProbe.Services;

public class DesktopSecretStorage : ISecretStorage
{
    private readonly string _secretsDir;

    public DesktopSecretStorage()
    {
        _secretsDir = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "mqttprobe", "secrets");
        Directory.CreateDirectory(_secretsDir);
    }

    public Task<string?> GetAsync(string key)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        var bytes = File.ReadAllBytes(path);
        var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(decrypted));
    }

    public Task SetAsync(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            RemoveAsync(key).Wait();
            return Task.CompletedTask;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetPath(key), encrypted);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        var path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetPath(string key)
    {
        var safe = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .Replace('/', '_').Replace('+', '-').TrimEnd('=');
        return Path.Combine(_secretsDir, safe + ".dat");
    }
}
