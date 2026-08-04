using System.Text.Json;
using MqttProbe.Models.Configuration;

namespace MqttProbe.Services.Configuration;

// Excludes IDisposable deliberately: the composition root owns the document's lifetime,
// the collaborators only borrow it.
internal interface ISettingsDocument
{
    public AppConfiguration Config { get; }

    public Task MutateAndSaveAsync(Action<AppConfiguration> mutate);

    // SemaphoreSlim is not reentrant: never call this from inside the action.
    public Task ExclusiveAsync(Func<Task> action);

    // The caller must already hold the lock via ExclusiveAsync.
    public Task SaveAsync();
}

internal sealed class SettingsDocument(string path) : ISettingsDocument, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private AppConfiguration _config = new();

    public string Path => path;

    public bool Exists => File.Exists(path);

    public AppConfiguration Config
    {
        get => Volatile.Read(ref _config);
        set => Volatile.Write(ref _config, value);
    }

    // ConfigureAwait(false) on the I/O and lock primitives here, and throughout the
    // SettingsLoader.LoadAsync chain: Desktop startup blocks on LoadAsync with
    // GetAwaiter().GetResult(), so a captured context deadlocks against the blocking thread.
    // The facet mutators below are never blocked on and are deliberately left capturing.
    public async Task<AppConfiguration?> ReadAsync() =>
        JsonSerializer.Deserialize<AppConfiguration>(
            await File.ReadAllTextAsync(path).ConfigureAwait(false), _jsonOptions);

    public async Task ExclusiveAsync(Func<Task> action)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // SemaphoreSlim is not reentrant and the save re-acquires, so the mutation must
    // release before the save starts.
    public async Task MutateAndSaveAsync(Action<AppConfiguration> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            mutate(_config);
        }
        finally
        {
            _lock.Release();
        }

        await ExclusiveAsync(SaveAsync);
    }

    public async Task SaveAsync()
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
        await FileHelper.WriteAtomicallyAsync(path, JsonSerializer.Serialize(sanitised, _jsonOptions))
            .ConfigureAwait(false);
        RestrictFilePermissions(path);
    }

    private static void RestrictFilePermissions(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose() => _lock.Dispose();
}
