using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Configuration;

namespace MqttProbe.Services.Configuration;

internal sealed class SettingsLoader(
    SettingsDocument document,
    ConnectionSecrets secrets,
    bool isMobile,
    ILogger? logger) : ISettingsLoader
{
    private static readonly JsonSerializerOptions _migrationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

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
            var seeded = new AppConfiguration { Connections = ConfigDefaults.SeedConnections(), Sparkplug = new SparkplugSettings() };
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
            var json = await File.ReadAllTextAsync(document.Path).ConfigureAwait(false);
            document.Config = JsonSerializer.Deserialize<AppConfiguration>(json, _migrationOptions)
                ?? new AppConfiguration();
            configLoadedSuccessfully = true;

            // Pass raw JSON for migration only when sparkplug section was absent.
            var legacyJson = document.Config.Sparkplug is null ? json : null;
            ConfigDefaults.Normalize(document.Config, legacyJson);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load config from {Path}; using defaults.", document.Path);
            document.Config = new AppConfiguration();
            ConfigDefaults.Normalize(document.Config);
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
