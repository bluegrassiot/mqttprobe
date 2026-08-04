using Microsoft.Extensions.Logging;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Configuration;

// Wraps the optional ISecretStorage so callers never branch on null. Hosts that register no
// secret storage get a no-op: GetAsync yields null and the writes are silently skipped.
internal sealed class ConnectionSecrets(ISecretStorage? storage, ILogger? logger)
{
    public bool IsEnabled => storage is not null;

    public static string KeyFor(Connection connection)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(connection.Name);
        var hash = System.Security.Cryptography.SHA256.HashData(nameBytes);
        return $"mqtt_{Convert.ToHexString(hash)[..16]}";
    }

    public Task<string?> GetAsync(Connection connection) => GetAsync(KeyFor(connection));

    public Task<string?> GetAsync(string key) =>
        storage is null ? Task.FromResult<string?>(null) : storage.GetAsync(key);

    // An empty password is removed rather than stored, preserving the original behaviour.
    public async Task SetAsync(Connection connection)
    {
        if (storage is null) return;

        var key = KeyFor(connection);
        if (string.IsNullOrEmpty(connection.Password))
            await storage.RemoveAsync(key);
        else
            await storage.SetAsync(key, connection.Password);
    }

    public Task RemoveAsync(string key) =>
        storage is null ? Task.CompletedTask : storage.RemoveAsync(key);

    // Each step is guarded separately: failing to remove the new secret must not stop the
    // attempt to put the old one back.
    public async Task RestoreAfterUpsertFailureAsync(
        Connection attempted, string? oldKey, string? oldPassword)
    {
        if (storage is null) return;

        try
        {
            await storage.RemoveAsync(KeyFor(attempted));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "Failed to remove new secret key {Key} during rollback", KeyFor(attempted));
        }

        if (oldKey is null) return;

        try
        {
            if (oldPassword is not null)
                await storage.SetAsync(oldKey, oldPassword);
            else
                await storage.RemoveAsync(oldKey);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to restore old secret key {Key} during rollback", oldKey);
        }
    }

    public async Task RestoreAfterRemoveFailureAsync(Connection removed, string? value)
    {
        if (storage is null || value is null) return;

        try
        {
            await storage.SetAsync(KeyFor(removed), value);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex, "Failed to restore secret for removed connection {Name} during rollback",
                removed.Name);
        }
    }
}
