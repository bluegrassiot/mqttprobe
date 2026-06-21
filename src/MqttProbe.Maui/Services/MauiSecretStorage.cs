using MqttProbe.Services.Security;

namespace MqttProbe.Services;

public class MauiSecretStorage : ISecretStorage
{
    public Task<string?> GetAsync(string key) =>
        SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value)
    {
        // Windows PasswordVault (used by MAUI SecureStorage on Windows) rejects empty
        // passwords with ArgumentException. Treat empty as "nothing to store".
        if (string.IsNullOrEmpty(value))
        {
            SecureStorage.Default.Remove(key);
            return Task.CompletedTask;
        }
        return SecureStorage.Default.SetAsync(key, value);
    }

    public Task RemoveAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}
