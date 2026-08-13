using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Emulation;

namespace MqttProbe.Core.Services.Configuration;

internal sealed class EmulatorSettings(ISettingsDocument document) : IEmulatorSettings
{
    public event Action<Guid>? EmulatorsChanged;

    public IReadOnlyList<EmulatorNodeConfig> GetEmulatorNodes(Guid connectionId) =>
        document.Config.EmulatorsByConnection.TryGetValue(connectionId, out var doc)
            ? doc.Nodes
            : [];

    public int GetEmulatorPublishIntervalMs(Guid connectionId) =>
        document.Config.EmulatorsByConnection.TryGetValue(connectionId, out var doc)
            ? doc.PublishIntervalMs
            : 500;

    public async Task AddEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node)
    {
        await document.MutateAndSaveAsync(
            config => DocumentFor(config, connectionId).Nodes.Add(node));

        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task UpdateEmulatorNodeAsync(Guid connectionId, EmulatorNodeConfig node)
    {
        await document.MutateAndSaveAsync(config =>
        {
            if (config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
            {
                var idx = doc.Nodes.FindIndex(existing => existing.Id == node.Id);
                if (idx >= 0) doc.Nodes[idx] = node;
            }
        });

        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task RemoveEmulatorNodeAsync(Guid connectionId, Guid nodeId)
    {
        await document.MutateAndSaveAsync(config =>
        {
            if (config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
                doc.Nodes.RemoveAll(node => node.Id == nodeId);
        });

        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task RemoveAllEmulatorNodesAsync(Guid connectionId)
    {
        await document.MutateAndSaveAsync(config =>
        {
            if (config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
                doc.Nodes.Clear();
        });

        EmulatorsChanged?.Invoke(connectionId);
    }

    public async Task SetEmulatorPublishIntervalAsync(Guid connectionId, int intervalMs)
    {
        await document.MutateAndSaveAsync(
            config => DocumentFor(config, connectionId).PublishIntervalMs = intervalMs);

        EmulatorsChanged?.Invoke(connectionId);
    }

    private static EmulatorDocument DocumentFor(AppConfiguration config, Guid connectionId)
    {
        if (!config.EmulatorsByConnection.TryGetValue(connectionId, out var doc))
        {
            doc = new EmulatorDocument();
            config.EmulatorsByConnection[connectionId] = doc;
        }

        return doc;
    }
}
