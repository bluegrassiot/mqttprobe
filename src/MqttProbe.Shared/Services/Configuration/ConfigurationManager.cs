using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Configuration;

public interface IConfigurationManager
{
    public AppConfiguration Configuration { get; }
    public Task Load();
    public Task Save();
    public Task LoadSecretsAsync(ISecretStorage secretStorage);
    public Task AddConnection(Connection connection);
    public Task RemoveConnection(Connection connection);
    public bool VerifyCredentials(string username, string password);
    public Task SetPasswordAsync(string username, string newPassword);
    public bool IsHintDismissed(string hintId);
    public Task DismissHintAsync(string hintId);
    public Task SetFontAccessibleAsync(bool accessible);
}

public class ConfigurationManager(string configPath, ILogger<ConfigurationManager>? logger = null) : IConfigurationManager
{
    private readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private ISecretStorage? _secretStorage;

    public AppConfiguration Configuration { get; private set; } = new();

    private static string SecretKey(Connection c)
    {
        // Use a hash of the connection name to avoid collisions from different names
        // mapping to the same sanitized value (e.g., "production-db" and "production_db").
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(c.Name);
        var hash = System.Security.Cryptography.SHA256.HashData(nameBytes);
        var hashHex = Convert.ToHexString(hash)[..16];
        return $"mqtt_{hashHex}";
    }

    public bool VerifyCredentials(string username, string password)
    {
        if (string.IsNullOrEmpty(Configuration.Auth.PasswordHash)) return false;
        return string.Equals(username, Configuration.Auth.Username, StringComparison.Ordinal)
               && PasswordHasher.Verify(password, Configuration.Auth.PasswordHash);
    }

    public async Task SetPasswordAsync(string username, string newPassword)
    {
        Configuration.Auth.Username = username;
        Configuration.Auth.PasswordHash = PasswordHasher.Hash(newPassword);
        await Save();
    }

    public bool IsHintDismissed(string hintId) =>
        Configuration.Ui.DismissedHints.Contains(hintId);

    public async Task DismissHintAsync(string hintId)
    {
        if (Configuration.Ui.DismissedHints.Contains(hintId)) return;
        Configuration.Ui.DismissedHints.Add(hintId);
        await Save();
    }

    public async Task SetFontAccessibleAsync(bool accessible)
    {
        Configuration.Ui.FontAccessible = accessible;
        await Save();
    }

    public async Task AddConnection(Connection connection)
    {
        var existing = Configuration.Connections.Find(c => c.Name.Equals(connection.Name));
        if (existing != null) Configuration.Connections.Remove(existing);

        Configuration.Connections.Add(connection.Clone());

        if (_secretStorage != null)
        {
            if (string.IsNullOrEmpty(connection.Password))
                await _secretStorage.RemoveAsync(SecretKey(connection));
            else
                await _secretStorage.SetAsync(SecretKey(connection), connection.Password);
        }

        await Save();
    }

    public async Task RemoveConnection(Connection connection)
    {
        var existing = Configuration.Connections.Find(c => c.Name.Equals(connection.Name));
        if (existing != null)
        {
            Configuration.Connections.Remove(existing);

            if (_secretStorage != null)
                await _secretStorage.RemoveAsync(SecretKey(connection));

            await Save();
        }
    }

    // Saves everything except passwords, which live in ISecretStorage.
    public async Task Save()
    {
        await _saveLock.WaitAsync();
        try
        {
            var sanitised = new AppConfiguration
            {
                Auth = Configuration.Auth,
                Performance = Configuration.Performance,
                Ui = Configuration.Ui,
                Connections = Configuration.Connections.Select(c => c.CloneWithoutPassword()).ToList()
            };
            await FileHelper.WriteAtomicallyAsync(configPath, JsonSerializer.Serialize(sanitised, _jsonSerializerOptions));
            RestrictFilePermissions(configPath);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task Load()
    {
        if (!File.Exists(configPath))
        {
            var directoryPath = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);
            Configuration = NormalizeConfiguration(null);
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(Configuration, _jsonSerializerOptions));
            RestrictFilePermissions(configPath);
            return;
        }

        try
        {
            var configStr = await File.ReadAllTextAsync(configPath);
            Configuration = NormalizeConfiguration(JsonSerializer.Deserialize<AppConfiguration>(configStr));
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Config file at {Path} contains malformed JSON; using defaults.", configPath);
            Configuration = NormalizeConfiguration(null);
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "Failed to read config file at {Path}; using defaults.", configPath);
            Configuration = NormalizeConfiguration(null);
        }
    }

    public async Task LoadSecretsAsync(ISecretStorage secretStorage)
    {
        _secretStorage = secretStorage;

        foreach (var connection in Configuration.Connections)
        {
            var stored = await secretStorage.GetAsync(SecretKey(connection));
            if (stored != null)
            {
                connection.Password = stored;
            }
        }
    }

    private static AppConfiguration NormalizeConfiguration(AppConfiguration? configuration) =>
        new()
        {
            Auth = configuration?.Auth ?? new Auth(),
            Performance = configuration?.Performance ?? new PerformanceSettings(),
            Ui = configuration?.Ui ?? new UiPreferences(),
            Connections = configuration?.Connections ?? []
        };

    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
