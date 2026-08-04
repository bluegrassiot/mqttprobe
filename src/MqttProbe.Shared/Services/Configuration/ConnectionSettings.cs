using MqttProbe.Models.Chart;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Configuration;

internal sealed class ConnectionSettings(
    ISettingsDocument document,
    ConnectionSecrets secrets,
    ICertificateAssetStore? certStore) : IConnectionSettings
{
    public IReadOnlyList<Connection> Connections => document.Config.Connections;

    public async Task AddConnectionAsync(Connection connection)
    {
        await document.ExclusiveAsync(async () =>
        {
            var snapshot = CaptureSnapshot();
            string? previousPassword = null;
            string? oldSecretKey = null;
            bool configMutated = false;
            bool secretsMutated = false;

            try
            {
                (oldSecretKey, previousPassword) = await UpsertAsync(connection);
                configMutated = true;

                if (oldSecretKey is not null && oldSecretKey != ConnectionSecrets.KeyFor(connection))
                    await secrets.RemoveAsync(oldSecretKey);

                await secrets.SetAsync(connection);
                secretsMutated = true;

                await document.SaveAsync();
            }
            catch (Exception)
            {
                if (configMutated)
                    RestoreSnapshot(snapshot);

                if (secretsMutated)
                    await secrets.RestoreAfterUpsertFailureAsync(
                        connection, oldSecretKey, previousPassword);
                throw;
            }
        });
    }

    // Returns the replaced connection's secret key and password so the caller can roll them
    // back; both are null when this is an insert rather than a replace.
    private async Task<(string? OldSecretKey, string? PreviousPassword)> UpsertAsync(
        Connection connection)
    {
        var config = document.Config;
        var existingIdx = config.Connections.FindIndex(c => c.Id == connection.Id);
        if (existingIdx < 0)
        {
            config.Connections.Add(connection.Clone());
            return (null, null);
        }

        var existing = config.Connections[existingIdx];
        var oldSecretKey = ConnectionSecrets.KeyFor(existing);
        var previousPassword = await secrets.GetAsync(oldSecretKey);
        config.Connections[existingIdx] = connection.Clone();
        return (oldSecretKey, previousPassword);
    }

    public async Task RemoveConnectionAsync(Connection connection)
    {
        Connection? removed = null;

        await document.ExclusiveAsync(async () =>
        {
            var config = document.Config;
            var snapshot = CaptureSnapshot();
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
                    removedSecretValue = await secrets.GetAsync(removed);
                    await secrets.RemoveAsync(ConnectionSecrets.KeyFor(removed));
                }

                await document.SaveAsync();
            }
            catch (Exception)
            {
                RestoreSnapshot(snapshot);

                if (removed is not null)
                    await secrets.RestoreAfterRemoveFailureAsync(removed, removedSecretValue);
                throw;
            }
        });

        // After successful persistence, delete the associated cert asset (best-effort)
        if (removed?.ClientCertificateAssetId is not null && certStore is not null)
        {
            try { await certStore.DeleteAsync(removed.Id, removed.ClientCertificateAssetId); } catch { /* config already persisted; the orphan sweep at next startup deletes it */ }
        }
    }

    private sealed record Snapshot(
        List<Connection> Connections,
        Dictionary<Guid, List<ChartConfiguration>> Charts,
        Dictionary<Guid, EmulatorDocument> Emulators);

    private Snapshot CaptureSnapshot()
    {
        var config = document.Config;
        return new Snapshot(
            config.Connections.Select(c => c.Clone()).ToList(),
            config.ChartsByConnection.ToDictionary(kv => kv.Key, kv => new List<ChartConfiguration>(kv.Value)),
            config.EmulatorsByConnection.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private void RestoreSnapshot(Snapshot snapshot)
    {
        var config = document.Config;
        config.Connections = snapshot.Connections;
        config.ChartsByConnection = snapshot.Charts;
        config.EmulatorsByConnection = snapshot.Emulators;
    }
}
