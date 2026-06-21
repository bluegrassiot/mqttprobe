using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Chart;

namespace MqttProbe.Services.Chart;

public interface IChartConfigurationStore
{
    public event Action? ConfigurationsChanged;
    public IReadOnlyList<ChartConfiguration> Configurations { get; }
    public Task LoadAsync();
    public Task SaveAsync();
    public Task AddAsync(ChartConfiguration config);
    public Task UpdateAsync(ChartConfiguration config);
    public Task RemoveAsync(Guid configId);
}

public class ChartConfigurationStore(string filePath, ILogger<ChartConfigurationStore>? logger = null) : IChartConfigurationStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private ChartConfiguration[] _configurations = [];

    public event Action? ConfigurationsChanged;

    public IReadOnlyList<ChartConfiguration> Configurations => Volatile.Read(ref _configurations);

    public async Task LoadAsync()
    {
        await _mutationLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
            {
                _configurations = [];
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                _configurations = (JsonSerializer.Deserialize<List<ChartConfiguration>>(json, _jsonOptions) ?? [])
                    .ToArray();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Failed to load chart configurations from {FilePath}; using an empty chart configuration set.",
                    filePath);
                _configurations = [];
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _mutationLock.WaitAsync();
        try
        {
            await SaveSnapshotAsync(_configurations);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task SaveSnapshotAsync(IReadOnlyList<ChartConfiguration> snapshot)
    {
        await FileHelper.WriteAtomicallyAsync(filePath, JsonSerializer.Serialize(snapshot, _jsonOptions));
    }

    public async Task AddAsync(ChartConfiguration config)
    {
        await _mutationLock.WaitAsync();
        try
        {
            var snapshot = _configurations.Concat([config]).ToArray();
            _configurations = snapshot;
            await SaveSnapshotAsync(snapshot);
        }
        finally
        {
            _mutationLock.Release();
        }

        ConfigurationsChanged?.Invoke();
    }

    public async Task UpdateAsync(ChartConfiguration config)
    {
        await _mutationLock.WaitAsync();
        try
        {
            var snapshot = _configurations.ToArray();
            var idx = Array.FindIndex(snapshot, c => c.Id == config.Id);
            if (idx >= 0) snapshot[idx] = config;
            _configurations = snapshot;
            await SaveSnapshotAsync(snapshot);
        }
        finally
        {
            _mutationLock.Release();
        }

        ConfigurationsChanged?.Invoke();
    }

    public async Task RemoveAsync(Guid configId)
    {
        await _mutationLock.WaitAsync();
        try
        {
            var snapshot = _configurations.Where(c => c.Id != configId).ToArray();
            _configurations = snapshot;
            await SaveSnapshotAsync(snapshot);
        }
        finally
        {
            _mutationLock.Release();
        }

        ConfigurationsChanged?.Invoke();
    }
}
