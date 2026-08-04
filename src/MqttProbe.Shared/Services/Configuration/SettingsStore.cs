using Microsoft.Extensions.Logging;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Configuration;

public sealed class SettingsStore : ISettingsStore, IDisposable
{
    private readonly SettingsDocument _document;
    private readonly SettingsLoader _loader;
    private readonly ConnectionSettings _connections;
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
        var secrets = new ConnectionSecrets(secretStorage, logger);

        _document = new SettingsDocument(configPath);
        _loader = new SettingsLoader(_document, secrets, isMobile, logger);
        _connections = new ConnectionSettings(_document, secrets, certStore);
        _charts = new ChartSettings(_document);
        _emulators = new EmulatorSettings(_document);
        _preferences = new PreferenceSettings(_document);
    }

    public AppConfiguration Config => _document.Config;

    public Task<bool> LoadAsync() => _loader.LoadAsync();

    public IReadOnlyList<Connection> Connections => _connections.Connections;
    public Task AddConnectionAsync(Connection connection) => _connections.AddConnectionAsync(connection);
    public Task RemoveConnectionAsync(Connection connection) => _connections.RemoveConnectionAsync(connection);

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

    public void Dispose() => _document.Dispose();
}
