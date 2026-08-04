using Microsoft.Extensions.Logging;
using MqttProbe.Models.Configuration;

namespace MqttProbe.Services.Configuration;

internal sealed class SettingsLoader(
    SettingsDocument document,
    ConnectionSecrets secrets,
    bool isMobile,
    ILogger? logger) : ISettingsLoader
{
    public async Task<bool> LoadAsync()
    {
        var configLoadedSuccessfully = false;
        await document.ExclusiveAsync(async () =>
        {
            configLoadedSuccessfully = await LoadOrCreateConfigAsync().ConfigureAwait(false);
            await LoadSecretsAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        return configLoadedSuccessfully;
    }

    // Returns whether an existing config file was parsed, which gates the orphan and AEAD
    // sweeps. The flag is set immediately after deserialization, before normalizing and
    // migrating: a failure in either still counts as "config loaded", because the
    // connections it describes are known and their certificates must not be swept as orphans.
    private async Task<bool> LoadOrCreateConfigAsync()
    {
        if (!document.Exists)
        {
            var seeded = new AppConfiguration { Connections = ConfigDefaults.SeedConnections() };
            if (isMobile)
            {
                seeded.Performance.MaxStoredMessages = 1_000;
                seeded.Performance.MaxMessagesPerSecond = 1_000;
                seeded.Performance.MaxTopicNodes = 1_000;
                seeded.Performance.MaxDisplayMessages = 500;
            }
            document.Config = seeded;
            await document.SaveAsync().ConfigureAwait(false);
            return false;
        }

        var configLoadedSuccessfully = false;
        try
        {
            document.Config = await document.ReadAsync().ConfigureAwait(false) ?? new AppConfiguration();
            configLoadedSuccessfully = true;
            ConfigDefaults.Normalize(document.Config);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load config from {Path}; using defaults.", document.Path);
            document.Config = new AppConfiguration();
        }

        return configLoadedSuccessfully;
    }

    private async Task LoadSecretsAsync()
    {
        if (!secrets.IsEnabled) return;

        foreach (var connection in document.Config.Connections)
        {
            var stored = await secrets.GetAsync(connection).ConfigureAwait(false);
            if (stored != null)
                connection.Password = stored;
        }
    }
}
