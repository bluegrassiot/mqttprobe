using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Emulation;

namespace MqttProbe.Services.Emulation;

public interface IEmulatorConfigurationStore
{
    public event Action? NodesChanged;
    public IReadOnlyList<EmulatorNodeConfig> Nodes { get; }
    public int PublishIntervalMs { get; }
    public Task LoadAsync();
    public Task AddAsync(EmulatorNodeConfig node);
    public Task UpdateAsync(EmulatorNodeConfig node);
    public Task RemoveAsync(Guid nodeId);
    public Task RemoveAllAsync();
    public Task SetPublishIntervalAsync(int intervalMs);
}

public class EmulatorConfigurationStore(string filePath, ILogger<EmulatorConfigurationStore>? logger = null) : IEmulatorConfigurationStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private EmulatorDocument _document = new();

    public event Action? NodesChanged;

    public IReadOnlyList<EmulatorNodeConfig> Nodes => Volatile.Read(ref _document).Nodes;

    public int PublishIntervalMs => Volatile.Read(ref _document).PublishIntervalMs;

    public async Task LoadAsync()
    {
        await _mutationLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
            {
                _document = new EmulatorDocument();
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                _document = JsonSerializer.Deserialize<EmulatorDocument>(json, _jsonOptions) ?? new EmulatorDocument();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Failed to load emulator configurations from {FilePath}; using an empty emulator configuration set.",
                    filePath);
                _document = new EmulatorDocument();
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task AddAsync(EmulatorNodeConfig node)
    {
        await MutateAsync(doc => doc.Nodes.Add(node));
        NodesChanged?.Invoke();
    }

    public async Task UpdateAsync(EmulatorNodeConfig node)
    {
        await MutateAsync(doc =>
        {
            var idx = doc.Nodes.FindIndex(n => n.Id == node.Id);
            if (idx >= 0) doc.Nodes[idx] = node;
        });
        NodesChanged?.Invoke();
    }

    public async Task RemoveAsync(Guid nodeId)
    {
        await MutateAsync(doc => doc.Nodes.RemoveAll(n => n.Id == nodeId));
        NodesChanged?.Invoke();
    }

    public async Task RemoveAllAsync()
    {
        await MutateAsync(doc => doc.Nodes.Clear());
        NodesChanged?.Invoke();
    }

    public async Task SetPublishIntervalAsync(int intervalMs)
    {
        await MutateAsync(doc => doc.PublishIntervalMs = intervalMs);
        NodesChanged?.Invoke();
    }

    private async Task MutateAsync(Action<EmulatorDocument> mutation)
    {
        await _mutationLock.WaitAsync();
        try
        {
            // Mutations build a fresh document so readers keep a stable snapshot reference.
            var snapshot = new EmulatorDocument
            {
                Version = _document.Version,
                PublishIntervalMs = _document.PublishIntervalMs,
                Nodes = [.. _document.Nodes]
            };
            mutation(snapshot);
            _document = snapshot;
            await SaveSnapshotAsync(snapshot);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task SaveSnapshotAsync(EmulatorDocument snapshot)
    {
        await FileHelper.WriteAtomicallyAsync(filePath, JsonSerializer.Serialize(snapshot, _jsonOptions));
    }
}
