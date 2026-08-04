using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Configuration;

public interface IConnectionSettings
{
    public IReadOnlyList<Connection> Connections { get; }

    public Task AddConnectionAsync(Connection connection);
    public Task RemoveConnectionAsync(Connection connection);
}

public interface IChartSettings
{
    public event Action<Guid>? ChartsChanged;

    public IReadOnlyList<ChartConfiguration> GetCharts(Guid connectionId);
    public Task AddChartAsync(Guid connectionId, ChartConfiguration chart);
    public Task UpdateChartAsync(Guid connectionId, ChartConfiguration chart);
    public Task RemoveChartAsync(Guid connectionId, Guid chartId);
}

public interface IEmulatorSettings
{
    public event Action<Guid>? EmulatorsChanged;

    public IReadOnlyList<EmulatorNodeConfig> GetEmulatorNodes(Guid connectionId);
    public int GetEmulatorPublishIntervalMs(Guid connectionId);
    public Task AddEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node);
    public Task UpdateEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node);
    public Task RemoveEmulatorNodeAsync(Guid connectionId, Guid nodeId);
    public Task RemoveAllEmulatorNodesAsync(Guid connectionId);
    public Task SetEmulatorPublishIntervalAsync(Guid connectionId, int intervalMs);
}

public interface IUiSettings
{
    public event Action? UiPreferencesChanged;

    public UiPreferences Ui { get; }

    public Task SetThemeAsync(string theme);
    public Task SetFontFamilyAsync(string fontFamily);
    public Task SetFontAccessibleAsync(bool accessible);
    public Task SetAutoResubscribeAsync(bool autoResubscribe);
    public Task SetEnrichSparkplugAliasNamesAsync(bool enrich);
    public Task SetAutoRequestSparkplugRebirthAsync(bool autoRequest);
    public Task DismissHintAsync(string hintId);
    public bool IsHintDismissed(string hintId);
}

public interface IPerformanceSettings
{
    public event Action? PerformanceSettingsChanged;

    public PerformanceSettings Performance { get; }

    public Task SetMaxStoredMessagesAsync(int value);
    public Task SetMaxMessagesPerSecondAsync(int value);
    public Task SetMaxDisplayMessagesAsync(int value);
    public Task SetMaxTopicNodesAsync(int value);
}

public interface IAuthSettings
{
    public Auth Auth { get; }

    public bool VerifyCredentials(string username, string password);
    public Task SetPasswordAsync(string username, string newPassword);
}

public interface ISettingsLoader
{
    public Task<bool> LoadAsync();
}
