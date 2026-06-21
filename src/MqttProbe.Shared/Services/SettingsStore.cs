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
    public IReadOnlyList<ChartConfiguration> Charts { get; }
    public IReadOnlyList<EmulatorNodeConfig> EmulatorNodes { get; }
    public int EmulatorPublishIntervalMs { get; }

    public event Action? ChartsChanged;
    public event Action? EmulatorsChanged;

    public Task LoadAsync(ISecretStorage? secretStorage = null);
    public Task SaveAsync();

    // Connection ops
    public Task AddConnectionAsync(Connection connection);
    public Task RemoveConnectionAsync(Connection connection);

    // Chart ops
    public Task AddChartAsync(ChartConfiguration chart);
    public Task UpdateChartAsync(ChartConfiguration chart);
    public Task RemoveChartAsync(Guid chartId);

    // Emulator ops
    public Task AddEmulatorNodeAsync(EmulatorNodeConfig node);
    public Task UpdateEmulatorNodeAsync(EmulatorNodeConfig node);
    public Task RemoveEmulatorNodeAsync(Guid nodeId);
    public Task RemoveAllEmulatorNodesAsync();
    public Task SetEmulatorPublishIntervalAsync(int intervalMs);

    // UI prefs
    public Task SetThemeAsync(string theme);
    public Task SetFontFamilyAsync(string fontFamily);
    public Task SetFontAccessibleAsync(bool accessible);
    public Task SetAutoResubscribeAsync(bool autoResubscribe);
    public Task DismissHintAsync(string hintId);
    public bool IsHintDismissed(string hintId);

    public event Action? UiPreferencesChanged;

    // Performance prefs
    public event Action? PerformanceSettingsChanged;
    public Task SetMaxStoredMessagesAsync(int value);
    public Task SetMaxMessagesPerSecondAsync(int value);

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
    public IReadOnlyList<ChartConfiguration> Charts => Volatile.Read(ref _config).Charts;
    public IReadOnlyList<EmulatorNodeConfig> EmulatorNodes => Volatile.Read(ref _config).Emulators.Nodes;
    public int EmulatorPublishIntervalMs => Volatile.Read(ref _config).Emulators.PublishIntervalMs;

    public event Action? ChartsChanged;
    public event Action? EmulatorsChanged;
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
            Charts = _config.Charts,
            Emulators = _config.Emulators
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

    // --- Chart ops ---

    public async Task AddChartAsync(ChartConfiguration chart)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Charts.Add(chart);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        ChartsChanged?.Invoke();
    }

    public async Task UpdateChartAsync(ChartConfiguration chart)
    {
        await _lock.WaitAsync();
        try
        {
            var idx = _config.Charts.FindIndex(c => c.Id == chart.Id);
            if (idx >= 0) _config.Charts[idx] = chart;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        ChartsChanged?.Invoke();
    }

    public async Task RemoveChartAsync(Guid chartId)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Charts.RemoveAll(c => c.Id == chartId);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        ChartsChanged?.Invoke();
    }

    // --- Emulator ops ---

    public async Task AddEmulatorNodeAsync(EmulatorNodeConfig node)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Emulators.Nodes.Add(node);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke();
    }

    public async Task UpdateEmulatorNodeAsync(EmulatorNodeConfig node)
    {
        await _lock.WaitAsync();
        try
        {
            var idx = _config.Emulators.Nodes.FindIndex(n => n.Id == node.Id);
            if (idx >= 0) _config.Emulators.Nodes[idx] = node;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke();
    }

    public async Task RemoveEmulatorNodeAsync(Guid nodeId)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Emulators.Nodes.RemoveAll(n => n.Id == nodeId);
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke();
    }

    public async Task RemoveAllEmulatorNodesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _config.Emulators.Nodes.Clear();
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke();
    }

    public async Task SetEmulatorPublishIntervalAsync(int intervalMs)
    {
        await _lock.WaitAsync();
        try
        {
            _config.Emulators.PublishIntervalMs = intervalMs;
        }
        finally
        {
            _lock.Release();
        }

        await SaveAsync();
        EmulatorsChanged?.Invoke();
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
        _config.Charts ??= [];
        _config.Emulators ??= new EmulatorDocument();
        _config.Ui.DismissedHints ??= [];
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
