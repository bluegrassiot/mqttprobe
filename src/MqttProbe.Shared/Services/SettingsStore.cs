using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Configuration;

public interface ISettingsStore
{
    public AppConfiguration Config { get; }

    public event Action<Guid>? ChartsChanged;
    public event Action<Guid>? EmulatorsChanged;

    public Task LoadAsync(ISecretStorage? secretStorage = null);
    public Task SaveAsync();

    // Connection ops
    public Task AddConnectionAsync(Connection connection);
    public Task RemoveConnectionAsync(Connection connection);

    // Chart ops (per-connection)
    public Task AddChartAsync(Guid connectionId, ChartConfiguration chart);
    public Task UpdateChartAsync(Guid connectionId, ChartConfiguration chart);
    public Task RemoveChartAsync(Guid connectionId, Guid chartId);
    public IReadOnlyList<ChartConfiguration> GetCharts(Guid connectionId);

    // Emulator ops (per-connection)
    public Task AddEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node);
    public Task UpdateEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node);
    public Task RemoveEmulatorNodeAsync(Guid connectionId, Guid nodeId);
    public Task RemoveAllEmulatorNodesAsync(Guid connectionId);
    public Task SetEmulatorPublishIntervalAsync(Guid connectionId, int intervalMs);
    public IReadOnlyList<EmulatorNodeConfig> GetEmulatorNodes(Guid connectionId);
    public int GetEmulatorPublishIntervalMs(Guid connectionId);

    // UI prefs
    public Task SetThemeAsync(string theme);
    public Task SetFontFamilyAsync(string fontFamily);
    public Task SetFontAccessibleAsync(bool accessible);
    public Task SetAutoResubscribeAsync(bool autoResubscribe);
    public Task SetEnrichSparkplugAliasNamesAsync(bool enrich);
    public Task DismissHintAsync(string hintId);
    public bool IsHintDismissed(string hintId);

    public event Action? UiPreferencesChanged;

    // Performance prefs
    public event Action? PerformanceSettingsChanged;
    public Task SetMaxStoredMessagesAsync(int value);
    public Task SetMaxMessagesPerSecondAsync(int value);
    public Task SetMaxDisplayMessagesAsync(int value);
    public Task SetMaxTopicNodesAsync(int value);

    // Auth
    public bool VerifyCredentials(string username, string password);
    public Task SetPasswordAsync(string username, string newPassword);
}

public class SettingsStore : ISettingsStore
{
    private readonly string _configPath;
    private readonly ILogger<SettingsStore>? _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly SemaphoreSlim _lock = new(1, 1);

    private AppConfiguration _config = new();
    private ISecretStorage? _secretStorage;

    public SettingsStore(string configPath, ILogger<SettingsStore>? logger = null)
    {
        _configPath = configPath;
        _logger = logger;
    }

    public AppConfiguration Config => Volatile.Read(ref _config);

    public event Action<Guid>? ChartsChanged;
    public event Action<Guid>? EmulatorsChanged;
    public event Action? UiPreferencesChanged;
    public event Action? PerformanceSettingsChanged;

    public async Task LoadAsync(ISecretStorage? secretStorage = null)
    {
        await _lock.WaitAsync();
        try
        {
            _secretStorage = secretStorage;

            if (!File.Exists(_configPath))
            {
                _config = new AppConfiguration();
                await SaveCoreAsync();
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_configPath);
                _config = JsonSerializer.Deserialize<AppConfiguration>(json, _jsonOptions) ?? new AppConfiguration();
                NormalizeConfig();
                MigrateGlobalData();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load config from {Path}; using defaults.", _configPath);
                _config = new AppConfiguration();
            }

            if (_secretStorage != null)
                await LoadSecretsAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await SaveCoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveCoreAsync()
    {
        var sanitised = new AppConfiguration
        {
            Auth = _config.Auth,
            Performance = _config.Performance,
            Ui = _config.Ui,
            Connections = _config.Connections.Select(c => c.CloneWithoutPassword()).ToList(),
            ChartsByConnection = _config.ChartsByConnection,
            EmulatorsByConnection = _config.EmulatorsByConnection
        };
        await FileHelper.WriteAtomicallyAsync(_configPath, JsonSerializer.Serialize(sanitised, _jsonOptions));
        RestrictFilePermissions(_configPath);
    }

    // --- Connection ops ---

    public async Task AddConnectionAsync(Connection connection)
    {
        await _lock.WaitAsync();
        try
        {
            var existing = _config.Connections.Find(c => c.Name.Equals(connection.Name));
            if (existing != null) _config.Connections.Remove(existing);
            _config.Connections.Add(connection.Clone());

            if (_secretStorage != null)
            {
                if (string.IsNullOrEmpty(connection.Password))
                    await _secretStorage.RemoveAsync(SecretKey(connection));
                else
                    await _secretStorage.SetAsync(SecretKey(connection), connection.Password);
            }
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
    }

    public async Task RemoveConnectionAsync(Connection connection)
    {
        await _lock.WaitAsync();
        try
        {
            var existing = _config.Connections.Find(c => c.Name.Equals(connection.Name));
            if (existing != null)
            {
                _config.Connections.Remove(existing);
                _config.ChartsByConnection.Remove(existing.Id);
                _config.EmulatorsByConnection.Remove(existing.Id);
                if (_secretStorage != null)
                    await _secretStorage.RemoveAsync(SecretKey(connection));
            }
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
    }

    // --- Chart ops (per-connection) ---

    public async Task AddChartAsync(Guid connectionId, ChartConfiguration chart)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_config.ChartsByConnection.TryGetValue(connectionId, out var charts))
            {
                charts = [];
                _config.ChartsByConnection[connectionId] = charts;
            }

            charts.Add(chart);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        ChartsChanged?.Invoke(connectionId);
    }

    public async Task UpdateChartAsync(Guid connectionId, ChartConfiguration chart)
    {
        await _lock.WaitAsync();
        try
        {
            if (_config.ChartsByConnection.TryGetValue(connectionId, out var charts))
            {
                var idx = charts.FindIndex(c => c.Id == chart.Id);
                if (idx >= 0) charts[idx] = chart;
            }
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        ChartsChanged?.Invoke(connectionId);
    }

    public async Task RemoveChartAsync(Guid connectionId, Guid chartId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_config.ChartsByConnection.TryGetValue(connectionId, out var charts))
                charts.RemoveAll(c => c.Id == chartId);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        ChartsChanged?.Invoke(connectionId);
    }

    public IReadOnlyList<ChartConfiguration> GetCharts(Guid connectionId)
    {
        return _config.ChartsByConnection.TryGetValue(connectionId, out var charts)
            ? charts
            : [];
    }

    // --- Emulator ops (per-connection) ---

    public async Task AddEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
            {
                doc = new EmulatorDocument();
                _config.EmulatorsByConnection[connectionId] = doc;
            }

            doc.Nodes.Add(node);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task UpdateEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node)
    {
        await _lock.WaitAsync();
        try
        {
            if (_config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
            {
                var idx = doc.Nodes.FindIndex(n => n.Id == node.Id);
                if (idx >= 0) doc.Nodes[idx] = node;
            }
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task RemoveEmulatorNodeAsync(Guid connectionId, Guid nodeId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
                doc.Nodes.RemoveAll(n => n.Id == nodeId);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task RemoveAllEmulatorNodesAsync(Guid connectionId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
                doc.Nodes.Clear();
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task SetEmulatorPublishIntervalAsync(Guid connectionId, int intervalMs)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
            {
                doc = new EmulatorDocument();
                _config.EmulatorsByConnection[connectionId] = doc;
            }

            doc.PublishIntervalMs = intervalMs;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke(connectionId);
    }

    public IReadOnlyList<EmulatorNodeConfig> GetEmulatorNodes(Guid connectionId)
    {
        return _config.EmulatorsByConnection.TryGetValue(connectionId, out var doc)
            ? doc.Nodes
            : [];
    }

    public int GetEmulatorPublishIntervalMs(Guid connectionId)
    {
        return _config.EmulatorsByConnection.TryGetValue(connectionId, out var doc)
            ? doc.PublishIntervalMs
            : 500;
    }

    // --- Performance prefs ---

    public async Task SetMaxStoredMessagesAsync(int value)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Performance.MaxStoredMessages = value;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        PerformanceSettingsChanged?.Invoke();
    }

    public async Task SetMaxMessagesPerSecondAsync(int value)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Performance.MaxMessagesPerSecond = value;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        PerformanceSettingsChanged?.Invoke();
    }

    public async Task SetMaxDisplayMessagesAsync(int value)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Performance.MaxDisplayMessages = value;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        PerformanceSettingsChanged?.Invoke();
    }

    public async Task SetMaxTopicNodesAsync(int value)
    {
        if (value < 100) return;

        await _lock.WaitAsync();
        try
        {
            _config.Performance.MaxTopicNodes = value;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        PerformanceSettingsChanged?.Invoke();
    }

    // --- UI prefs ---

    public async Task SetThemeAsync(string theme)
    {
        _config.Ui.Theme = theme;
        await SaveAsync();
        UiPreferencesChanged?.Invoke();
    }

    public async Task SetFontFamilyAsync(string fontFamily)
    {
        _config.Ui.FontFamily = fontFamily;
        await SaveAsync();
        UiPreferencesChanged?.Invoke();
    }

    public async Task SetFontAccessibleAsync(bool accessible)
    {
        _config.Ui.FontAccessible = accessible;
        await SaveAsync();
        UiPreferencesChanged?.Invoke();
    }

    public async Task SetAutoResubscribeAsync(bool autoResubscribe)
    {
        _config.Ui.AutoResubscribe = autoResubscribe;
        await SaveAsync();
        UiPreferencesChanged?.Invoke();
    }

    public async Task SetEnrichSparkplugAliasNamesAsync(bool enrich)
    {
        _config.Ui.EnrichSparkplugAliasNames = enrich;
        await SaveAsync();
        UiPreferencesChanged?.Invoke();
    }

    public async Task DismissHintAsync(string hintId)
    {
        if (_config.Ui.DismissedHints.Contains(hintId)) return;
        _config.Ui.DismissedHints.Add(hintId);
        await SaveAsync();
        UiPreferencesChanged?.Invoke();
    }

    public bool IsHintDismissed(string hintId) =>
        _config.Ui.DismissedHints.Contains(hintId);

    // --- Auth ---

    public bool VerifyCredentials(string username, string password)
    {
        if (string.IsNullOrEmpty(_config.Auth.PasswordHash)) return false;
        return string.Equals(username, _config.Auth.Username, StringComparison.Ordinal)
               && PasswordHasher.Verify(password, _config.Auth.PasswordHash);
    }

    public async Task SetPasswordAsync(string username, string newPassword)
    {
        _config.Auth.Username = username;
        _config.Auth.PasswordHash = PasswordHasher.Hash(newPassword);
        await SaveAsync();
    }

    // --- Internal ---

    private async Task LoadSecretsAsync()
    {
        if (_secretStorage == null) return;
        foreach (var connection in _config.Connections)
        {
            var stored = await _secretStorage.GetAsync(SecretKey(connection));
            if (stored != null)
                connection.Password = stored;
        }
    }

    private void NormalizeConfig()
    {
        _config.Connections ??= [];
        _config.Auth ??= new Auth();
        _config.Performance ??= new PerformanceSettings();
        _config.Ui ??= new UiPreferences();
#pragma warning disable CS0618 // Intentional: ensuring legacy properties exist for migration
        _config.Charts ??= [];
        _config.Emulators ??= new EmulatorDocument();
#pragma warning restore CS0618
        _config.Ui.DismissedHints ??= [];
        _config.ChartsByConnection ??= [];
        _config.EmulatorsByConnection ??= [];
    }

    private void MigrateGlobalData()
    {
#pragma warning disable CS0618 // Intentional: migrating from legacy properties
        if (_config.ChartsByConnection.Count == 0 && _config.Charts.Count > 0)
        {
            var targetId = _config.Connections.FirstOrDefault()?.Id
                           ?? EnsureDefaultConnection();
            _config.ChartsByConnection[targetId] = _config.Charts;
            _config.Charts = [];
        }

        if (_config.EmulatorsByConnection.Count == 0
            && _config.Emulators.Nodes.Count > 0)
        {
            var targetId = _config.Connections.FirstOrDefault()?.Id
                           ?? EnsureDefaultConnection();
            _config.EmulatorsByConnection[targetId] = _config.Emulators;
            _config.Emulators = new EmulatorDocument();
        }
#pragma warning restore CS0618
    }

    private Guid EnsureDefaultConnection()
    {
        var id = Guid.NewGuid();
        _config.Connections.Add(new Connection
        {
            Id = id,
            Name = "Default"
        });
        return id;
    }

    private static string SecretKey(Connection c)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(c.Name);
        var hash = System.Security.Cryptography.SHA256.HashData(nameBytes);
        var hashHex = Convert.ToHexString(hash)[..16];
        return $"mqtt_{hashHex}";
    }

    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
