using Microsoft.Extensions.Logging;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Configuration;

public class SettingsStore : ISettingsStore, IDisposable
{
    private readonly ILogger<SettingsStore>? _logger;
    private readonly bool _isMobile;
    private readonly ConnectionSecrets _secrets;
    private readonly ICertificateAssetStore? _certStore;

    private readonly SettingsDocument _document;
    private readonly ChartSettings _charts;
    private readonly EmulatorSettings _emulators;
    private readonly PreferenceSettings _preferences;

    // secretStorage and certStore trail isMobile and logger so the existing
    // construction sites keep compiling.
    public SettingsStore(
        string configPath,
        bool isMobile = false,
        ILogger<SettingsStore>? logger = null,
        ISecretStorage? secretStorage = null,
        ICertificateAssetStore? certStore = null)
    {
        _isMobile = isMobile;
        _logger = logger;
        _secrets = new ConnectionSecrets(secretStorage, logger);
        _certStore = certStore;

        _document = new SettingsDocument(configPath);
        _charts = new ChartSettings(_document);
        _emulators = new EmulatorSettings(_document);
        _preferences = new PreferenceSettings(_document);
    }

    public AppConfiguration Config => _document.Config;

    public IReadOnlyList<Connection> Connections => Config.Connections;

    public async Task<bool> LoadAsync()
    {
        var configLoadedSuccessfully = false;
        await _document.ExclusiveAsync(async () =>
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
        if (!_document.Exists)
        {
            var seeded = new AppConfiguration { Connections = ConfigDefaults.SeedConnections() };
            if (_isMobile)
            {
                seeded.Performance.MaxStoredMessages = 1_000;
                seeded.Performance.MaxMessagesPerSecond = 1_000;
                seeded.Performance.MaxTopicNodes = 1_000;
                seeded.Performance.MaxDisplayMessages = 500;
            }
            _document.Config = seeded;
            await SaveCoreAsync().ConfigureAwait(false);
            return false;
        }

        var configLoadedSuccessfully = false;
        try
        {
            _document.Config = await _document.ReadAsync().ConfigureAwait(false) ?? new AppConfiguration();
            configLoadedSuccessfully = true;
            ConfigDefaults.Normalize(_document.Config);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load config from {Path}; using defaults.", _document.Path);
            _document.Config = new AppConfiguration();
        }

        return configLoadedSuccessfully;
    }

    // Virtual so a test subclass can force the connection-op save to fail.
    // save through the document directly and do not pass through this override.
    protected virtual Task SaveCoreAsync() => _document.WriteAsync();

    // --- Connection ops ---

    public async Task AddConnectionAsync(Connection connection)
    {
        await _document.ExclusiveAsync(async () =>
        {
            var snapshot = CaptureConnectionSnapshot();
            string? previousPassword = null;
            string? oldSecretKey = null;
            bool configMutated = false;
            bool secretsMutated = false;

            try
            {
                (oldSecretKey, previousPassword) = await UpsertConnectionAsync(connection);
                configMutated = true;

                if (oldSecretKey is not null && oldSecretKey != ConnectionSecrets.KeyFor(connection))
                    await _secrets.RemoveAsync(oldSecretKey);

                await _secrets.SetAsync(connection);
                secretsMutated = true;

                await SaveCoreAsync();
            }
            catch (Exception)
            {
                if (configMutated)
                {
                    RestoreConnectionSnapshot(snapshot);
                }

                if (secretsMutated)
                {
                    await _secrets.RestoreAfterUpsertFailureAsync(
                        connection, oldSecretKey, previousPassword);
                }
                throw;
            }
        });
    }

    // Returns the replaced connection's secret key and password so the caller can roll them
    // back; both are null when this is an insert rather than a replace.
    private async Task<(string? OldSecretKey, string? PreviousPassword)> UpsertConnectionAsync(
        Connection connection)
    {
        var config = _document.Config;
        var existingIdx = config.Connections.FindIndex(c => c.Id == connection.Id);
        if (existingIdx < 0)
        {
            config.Connections.Add(connection.Clone());
            return (null, null);
        }

        var existing = config.Connections[existingIdx];
        var oldSecretKey = ConnectionSecrets.KeyFor(existing);
        var previousPassword = await _secrets.GetAsync(oldSecretKey);
        config.Connections[existingIdx] = connection.Clone();
        return (oldSecretKey, previousPassword);
    }

    private sealed record ConnectionSnapshot(
        List<Connection> Connections,
        Dictionary<Guid, List<ChartConfiguration>> Charts,
        Dictionary<Guid, EmulatorDocument> Emulators);

    private ConnectionSnapshot CaptureConnectionSnapshot()
    {
        var config = _document.Config;
        return new ConnectionSnapshot(
            config.Connections.Select(c => c.Clone()).ToList(),
            config.ChartsByConnection.ToDictionary(kv => kv.Key, kv => new List<ChartConfiguration>(kv.Value)),
            config.EmulatorsByConnection.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private void RestoreConnectionSnapshot(ConnectionSnapshot snapshot)
    {
        var config = _document.Config;
        config.Connections = snapshot.Connections;
        config.ChartsByConnection = snapshot.Charts;
        config.EmulatorsByConnection = snapshot.Emulators;
    }

    public async Task RemoveConnectionAsync(Connection connection)
    {
        Connection? removed = null;

        await _document.ExclusiveAsync(async () =>
        {
            var config = _document.Config;
            var snapshot = CaptureConnectionSnapshot();
            string? removedSecretValue = null;

            try
            {
                var existing = config.Connections.FindIndex(c => c.Id == connection.Id);
                if (existing >= 0)
                {
                    removed = config.Connections[existing];
                    config.Connections.RemoveAt(existing);
                    config.ChartsByConnection.Remove(removed.Id);
                    config.EmulatorsByConnection.Remove(removed.Id);
                    removedSecretValue = await _secrets.GetAsync(removed);
                    await _secrets.RemoveAsync(ConnectionSecrets.KeyFor(removed));
                }

                await SaveCoreAsync();
            }
            catch (Exception)
            {
                RestoreConnectionSnapshot(snapshot);

                if (removed is not null)
                    await _secrets.RestoreAfterRemoveFailureAsync(removed, removedSecretValue);
                throw;
            }
        });

        // After successful persistence, delete the associated cert asset (best-effort)
        if (removed?.ClientCertificateAssetId is not null && _certStore is not null)
        {
            try { await _certStore.DeleteAsync(removed.Id, removed.ClientCertificateAssetId); } catch { /* config already persisted; the orphan sweep at next startup deletes it */ }
        }
    }

    private async Task LoadSecretsAsync()
    {
        if (!_secrets.IsEnabled) return;
        foreach (var connection in _document.Config.Connections)
        {
            var stored = await _secrets.GetAsync(connection).ConfigureAwait(false);
            if (stored != null)
                connection.Password = stored;
        }
    }

    public event Action<Guid>? ChartsChanged
    {
        add => _charts.ChartsChanged += value;
        remove => _charts.ChartsChanged -= value;
    }

    public event Action<Guid>? EmulatorsChanged
    {
        add => _emulators.EmulatorsChanged += value;
        remove => _emulators.EmulatorsChanged -= value;
    }

    public event Action? UiPreferencesChanged
    {
        add => _preferences.UiPreferencesChanged += value;
        remove => _preferences.UiPreferencesChanged -= value;
    }

    public event Action? PerformanceSettingsChanged
    {
        add => _preferences.PerformanceSettingsChanged += value;
        remove => _preferences.PerformanceSettingsChanged -= value;
    }

    public UiPreferences Ui => _preferences.Ui;
    public PerformanceSettings Performance => _preferences.Performance;
    public Auth Auth => _preferences.Auth;

    public IReadOnlyList<ChartConfiguration> GetCharts(Guid connectionId) => _charts.GetCharts(connectionId);
    public Task AddChartAsync(Guid connectionId, ChartConfiguration chart) => _charts.AddChartAsync(connectionId, chart);
    public Task UpdateChartAsync(Guid connectionId, ChartConfiguration chart) => _charts.UpdateChartAsync(connectionId, chart);
    public Task RemoveChartAsync(Guid connectionId, Guid chartId) => _charts.RemoveChartAsync(connectionId, chartId);

    public IReadOnlyList<EmulatorNodeConfig> GetEmulatorNodes(Guid connectionId) => _emulators.GetEmulatorNodes(connectionId);
    public int GetEmulatorPublishIntervalMs(Guid connectionId) => _emulators.GetEmulatorPublishIntervalMs(connectionId);
    public Task AddEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node) => _emulators.AddEmulatorNodeAsync(connectionId, node);
    public Task UpdateEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node) => _emulators.UpdateEmulatorNodeAsync(connectionId, node);
    public Task RemoveEmulatorNodeAsync(Guid connectionId, Guid nodeId) => _emulators.RemoveEmulatorNodeAsync(connectionId, nodeId);
    public Task RemoveAllEmulatorNodesAsync(Guid connectionId) => _emulators.RemoveAllEmulatorNodesAsync(connectionId);
    public Task SetEmulatorPublishIntervalAsync(Guid connectionId, int intervalMs) => _emulators.SetEmulatorPublishIntervalAsync(connectionId, intervalMs);

    public Task SetThemeAsync(string theme) => _preferences.SetThemeAsync(theme);
    public Task SetFontFamilyAsync(string fontFamily) => _preferences.SetFontFamilyAsync(fontFamily);
    public Task SetFontAccessibleAsync(bool accessible) => _preferences.SetFontAccessibleAsync(accessible);
    public Task SetAutoResubscribeAsync(bool autoResubscribe) => _preferences.SetAutoResubscribeAsync(autoResubscribe);
    public Task SetEnrichSparkplugAliasNamesAsync(bool enrich) => _preferences.SetEnrichSparkplugAliasNamesAsync(enrich);
    public Task SetAutoRequestSparkplugRebirthAsync(bool autoRequest) => _preferences.SetAutoRequestSparkplugRebirthAsync(autoRequest);
    public Task DismissHintAsync(string hintId) => _preferences.DismissHintAsync(hintId);
    public bool IsHintDismissed(string hintId) => _preferences.IsHintDismissed(hintId);

    public Task SetMaxStoredMessagesAsync(int value) => _preferences.SetMaxStoredMessagesAsync(value);
    public Task SetMaxMessagesPerSecondAsync(int value) => _preferences.SetMaxMessagesPerSecondAsync(value);
    public Task SetMaxDisplayMessagesAsync(int value) => _preferences.SetMaxDisplayMessagesAsync(value);
    public Task SetMaxTopicNodesAsync(int value) => _preferences.SetMaxTopicNodesAsync(value);

    public bool VerifyCredentials(string username, string password) => _preferences.VerifyCredentials(username, password);
    public Task SetPasswordAsync(string username, string newPassword) => _preferences.SetPasswordAsync(username, newPassword);

    private bool _disposed;

    // CA1001: the type owns the document, which owns a SemaphoreSlim. These are app-lifetime
    // singletons, so this only runs at container teardown, but leaving the handle undisposed
    // is still a leak. Full Dispose(bool) pattern because the type is not sealed (S3881).
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _document.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
