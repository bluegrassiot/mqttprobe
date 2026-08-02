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

    public Task LoadAsync(ISecretStorage? secretStorage = null, ICertificateAssetStore? certStore = null, ICertificateEnvelopeKeyStore? envelopeKeyStore = null);
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

public class SettingsStore : ISettingsStore, IDisposable
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
    private readonly bool _isMobile;

    private AppConfiguration _config = new();
    private ISecretStorage? _secretStorage;
    private ICertificateAssetStore? _certStore;

    public SettingsStore(string configPath, bool isMobile = false, ILogger<SettingsStore>? logger = null)
    {
        _configPath = configPath;
        _isMobile = isMobile;
        _logger = logger;
    }

    public AppConfiguration Config => Volatile.Read(ref _config);

    public event Action<Guid>? ChartsChanged;
    public event Action<Guid>? EmulatorsChanged;
    public event Action? UiPreferencesChanged;
    public event Action? PerformanceSettingsChanged;

    public async Task LoadAsync(ISecretStorage? secretStorage = null, ICertificateAssetStore? certStore = null, ICertificateEnvelopeKeyStore? envelopeKeyStore = null)
    {
        await _lock.WaitAsync();
        try
        {
            _secretStorage = secretStorage;
            _certStore = certStore;

            var configLoadedSuccessfully = await LoadOrCreateConfigAsync();

            if (_secretStorage != null)
                await LoadSecretsAsync();

            // Certificate store cleanup
            if (certStore is not null && envelopeKeyStore is not null
                && Directory.Exists(certStore.CertificatesDirectory))
            {
                await CleanCertificateStoreAsync(certStore, envelopeKeyStore, configLoadedSuccessfully);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // Returns whether an existing config file was parsed, which gates the orphan and AEAD
    // sweeps. The flag is set immediately after deserialization, before NormalizeConfig and
    // MigrateGlobalData: a failure in either still counts as "config loaded", because the
    // connections it describes are known and their certificates must not be swept as orphans.
    private async Task<bool> LoadOrCreateConfigAsync()
    {
        if (!File.Exists(_configPath))
        {
            _config = new AppConfiguration { Connections = CreateDefaultConnections() };
            if (_isMobile)
            {
                _config.Performance.MaxStoredMessages = 1_000;
                _config.Performance.MaxMessagesPerSecond = 1_000;
                _config.Performance.MaxTopicNodes = 1_000;
                _config.Performance.MaxDisplayMessages = 500;
            }
            await SaveCoreAsync();
            return false;
        }

        var configLoadedSuccessfully = false;
        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            _config = JsonSerializer.Deserialize<AppConfiguration>(json, _jsonOptions) ?? new AppConfiguration();
            configLoadedSuccessfully = true;
            NormalizeConfig();
            MigrateGlobalData();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load config from {Path}; using defaults.", _configPath);
            _config = new AppConfiguration();
        }

        return configLoadedSuccessfully;
    }

    // Stage order is load-bearing: staging temps go before their retry markers, and both
    // before the orphan sweep, which would otherwise see half-written assets as orphans.
    private async Task CleanCertificateStoreAsync(
        ICertificateAssetStore certStore,
        ICertificateEnvelopeKeyStore envelopeKeyStore,
        bool configLoadedSuccessfully)
    {
        // Staging cleanup ALWAYS runs regardless of config state:

        // 1. Delete staging files (.bin.tmp).
        await DeleteStagingFilesAsync(certStore, envelopeKeyStore);

        // 2. Delete cleanup-retry markers.
        await DeleteCleanupRetryMarkersAsync(certStore, envelopeKeyStore);

        // 3. Delete quarantine files older than 1 hour.
        await DeleteAgedQuarantineFilesAsync(certStore, envelopeKeyStore);

        // Orphan + AEAD cleanup ONLY when config loaded successfully
        if (!configLoadedSuccessfully)
        {
            _logger?.LogWarning(
                "Config file was missing or corrupt; skipping orphan and AEAD verification cleanup. " + // DevSkim: ignore DS187371
                "Staging temp files, retry markers, and aged quarantine files were still cleaned. " +
                "Verified certificate assets were preserved. Re-import certificates if needed.");
            return;
        }

        var knownPairs = await certStore.ListAssetsAsync();
        await DeleteUnconfiguredAssetsAsync(certStore, knownPairs);
        await SweepUnverifiedBlobsAsync(certStore, envelopeKeyStore, knownPairs);
    }

    private async Task DeleteStagingFilesAsync(
        ICertificateAssetStore certStore, ICertificateEnvelopeKeyStore envelopeKeyStore)
    {
        foreach (var tmpFile in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.bin.tmp"))
        {
            var tmpAssetId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(tmpFile))
                ["cert-".Length..];
            try
            {
                File.Delete(tmpFile);
                try { await envelopeKeyStore.RemoveAsync($"cert-env-{tmpAssetId}"); } catch { /* best-effort; retried next startup */ }
                var staleMarker = Path.Combine(certStore.CertificatesDirectory, $"cert-{tmpAssetId}.cleanup-retry");
                if (File.Exists(staleMarker))
                    try { File.Delete(staleMarker); } catch { /* best-effort; retried next startup */ }
            }
            catch
            {
                await QuarantineStagingFileAsync(certStore, envelopeKeyStore, tmpFile, tmpAssetId);
            }
        }
    }

    // Last resort for a staging temp that could not be deleted: move it aside so it is never
    // mistaken for a live asset, and if even that fails, leave a marker and shout.
    private async Task QuarantineStagingFileAsync(
        ICertificateAssetStore certStore,
        ICertificateEnvelopeKeyStore envelopeKeyStore,
        string tmpFile,
        string tmpAssetId)
    {
        var quarantinePath = Path.Combine(certStore.CertificatesDirectory, $"cert-{tmpAssetId}.quarantine");
        try { File.Delete(quarantinePath); } catch { /* stale quarantine file; the move below overwrites or fails loudly */ }
        bool renamed = false;
        try { File.Move(tmpFile, quarantinePath); renamed = true; } catch { /* handled via the renamed flag below */ }
        if (!renamed)
        {
            var retryMarker = Path.Combine(certStore.CertificatesDirectory, $"cert-{tmpAssetId}.cleanup-retry");
            if (!File.Exists(retryMarker))
                try { await File.WriteAllTextAsync(retryMarker, $"staging cleanup failed at {DateTime.UtcNow:o}"); } catch { /* marker is only a retry hint; the LogCritical below is the real signal */ }
            _logger?.LogCritical(
                "Could not delete or quarantine staging temp {Path}. Cleanup retry scheduled.",
                tmpFile);
        }
        else
        {
            _logger?.LogWarning("Could not delete staging temp {Path}; quarantined.", tmpFile);
            try { await envelopeKeyStore.RemoveAsync($"cert-env-{tmpAssetId}"); } catch { /* best-effort; retried next startup */ }
        }
    }

    private static async Task DeleteCleanupRetryMarkersAsync(
        ICertificateAssetStore certStore, ICertificateEnvelopeKeyStore envelopeKeyStore)
    {
        foreach (var marker in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.cleanup-retry"))
        {
            var markerAssetId = Path.GetFileNameWithoutExtension(marker)["cert-".Length..];
            var correspondingTmp = Path.Combine(certStore.CertificatesDirectory, $"cert-{markerAssetId}.bin.tmp");
            if (!File.Exists(correspondingTmp))
            {
                try { File.Delete(marker); } catch { /* best-effort; retried next startup */ }
                try { await envelopeKeyStore.RemoveAsync($"cert-env-{markerAssetId}"); } catch { /* best-effort; retried next startup */ }
            }
        }
    }

    private async Task DeleteAgedQuarantineFilesAsync(
        ICertificateAssetStore certStore, ICertificateEnvelopeKeyStore envelopeKeyStore)
    {
        foreach (var qFile in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.quarantine"))
        {
            try
            {
                var creationTime = File.GetCreationTime(qFile);
                if (creationTime < DateTime.Now.AddHours(-1))
                {
                    File.Delete(qFile);
                    var qAssetId = Path.GetFileNameWithoutExtension(qFile)["cert-".Length..];
                    try { await envelopeKeyStore.RemoveAsync($"cert-env-{qAssetId}"); } catch { /* best-effort; retried next startup */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to process quarantine file {Path}", qFile);
            }
        }
    }

    private async Task DeleteUnconfiguredAssetsAsync(
        ICertificateAssetStore certStore, IReadOnlyList<(Guid OwnerId, string AssetId)> knownPairs)
    {
        var configuredPairs = Config.Connections
            .Where(c => c.ClientCertificateAssetId is not null)
            .Select(c => (c.Id, c.ClientCertificateAssetId!))
            .ToHashSet();
        foreach (var (ownerId, assetId) in knownPairs)
        {
            if (!configuredPairs.Contains((ownerId, assetId)))
            {
                try { await certStore.DeleteAsync(ownerId, assetId); } catch { /* orphan sweep is best-effort; retried next startup */ }
            }
        }
    }

    // Blobs ListAssetsAsync could not verify: delete them unless the header names a connection
    // we still know about, in which case preserve and shout rather than destroy something the
    // user may still be able to recover.
    private async Task SweepUnverifiedBlobsAsync(
        ICertificateAssetStore certStore,
        ICertificateEnvelopeKeyStore envelopeKeyStore,
        IReadOnlyList<(Guid OwnerId, string AssetId)> knownPairs)
    {
        var verifiedAssetIds = knownPairs.Select(p => p.AssetId).ToHashSet(StringComparer.Ordinal);
        var knownOwnerIds = Config.Connections.Select(c => c.Id).ToHashSet();
        foreach (var binFile in Directory.EnumerateFiles(certStore.CertificatesDirectory, "cert-*.bin"))
        {
            var fileName = Path.GetFileName(binFile);
            if (fileName.EndsWith(".tmp", StringComparison.Ordinal)
                || fileName.EndsWith(".quarantine", StringComparison.Ordinal)
                || fileName.EndsWith(".cleanup-retry", StringComparison.Ordinal))
                continue;
            var fileAssetId = fileName["cert-".Length..^".bin".Length];
            if (verifiedAssetIds.Contains(fileAssetId))
                continue;

            try
            {
                var blob = await File.ReadAllBytesAsync(binFile);
                if (blob.Length < 73) { File.Delete(binFile); continue; }
                var headerOwner = System.Text.Encoding.ASCII.GetString(blob, 36, 36);
                if (Guid.TryParse(headerOwner, out var parsedOwner) && knownOwnerIds.Contains(parsedOwner))
                {
                    _logger?.LogCritical(
                        "Certificate blob {Path} has known owner {OwnerId} but failed AEAD verification. " + // DevSkim: ignore DS187371
                        "Preserving; re-import or manually delete.", binFile, parsedOwner);
                }
                else
                {
                    File.Delete(binFile);
                    try { await envelopeKeyStore.RemoveAsync($"cert-env-{fileAssetId}"); } catch { /* best-effort; retried next startup */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to process unverified blob {Path}", binFile);
            }
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

    protected virtual async Task SaveCoreAsync()
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
        var snapshot = CaptureConnectionSnapshot();
        string? previousPassword = null;
        string? oldSecretKey = null;
        bool configMutated = false;
        bool secretsMutated = false;

        try
        {
            (oldSecretKey, previousPassword) = await UpsertConnectionAsync(connection);
            configMutated = true;

            if (_secretStorage != null)
            {
                if (oldSecretKey is not null && oldSecretKey != SecretKey(connection))
                {
                    await _secretStorage.RemoveAsync(oldSecretKey);
                    secretsMutated = true;
                }

                if (string.IsNullOrEmpty(connection.Password))
                    await _secretStorage.RemoveAsync(SecretKey(connection));
                else
                    await _secretStorage.SetAsync(SecretKey(connection), connection.Password);
                secretsMutated = true;
            }

            await SaveCoreAsync();
        }
        catch (Exception)
        {
            if (configMutated)
            {
                RestoreConnectionSnapshot(snapshot);
            }

            if (secretsMutated && _secretStorage != null)
            {
                await RestoreSecretsAfterFailureAsync(
                    _secretStorage, connection, oldSecretKey, previousPassword);
            }
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Returns the replaced connection's secret key and password so the caller can roll them
    // back; both are null when this is an insert rather than a replace.
    private async Task<(string? OldSecretKey, string? PreviousPassword)> UpsertConnectionAsync(
        Connection connection)
    {
        var existingIdx = _config.Connections.FindIndex(c => c.Id == connection.Id);
        if (existingIdx < 0)
        {
            _config.Connections.Add(connection.Clone());
            return (null, null);
        }

        var existing = _config.Connections[existingIdx];
        var oldSecretKey = SecretKey(existing);
        var previousPassword = _secretStorage != null
            ? await _secretStorage.GetAsync(oldSecretKey)
            : null;
        _config.Connections[existingIdx] = connection.Clone();
        return (oldSecretKey, previousPassword);
    }

    private sealed record ConnectionSnapshot(
        List<Connection> Connections,
        Dictionary<Guid, List<ChartConfiguration>> Charts,
        Dictionary<Guid, EmulatorDocument> Emulators);

    private ConnectionSnapshot CaptureConnectionSnapshot() => new(
        _config.Connections.Select(c => c.Clone()).ToList(),
        _config.ChartsByConnection.ToDictionary(kv => kv.Key, kv => new List<ChartConfiguration>(kv.Value)),
        _config.EmulatorsByConnection.ToDictionary(kv => kv.Key, kv => kv.Value));

    private void RestoreConnectionSnapshot(ConnectionSnapshot snapshot)
    {
        _config.Connections = snapshot.Connections;
        _config.ChartsByConnection = snapshot.Charts;
        _config.EmulatorsByConnection = snapshot.Emulators;
    }

    // Each step is guarded separately: failing to remove the new secret must not stop the
    // attempt to put the old one back.
    private async Task RestoreSecretsAfterFailureAsync(
        ISecretStorage secretStorage, Connection connection, string? oldSecretKey, string? previousPassword)
    {
        try
        {
            await secretStorage.RemoveAsync(SecretKey(connection));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove new secret key {Key} during rollback",
                SecretKey(connection));
        }
        try
        {
            if (previousPassword is not null && oldSecretKey is not null)
                await secretStorage.SetAsync(oldSecretKey, previousPassword);
            else if (oldSecretKey is not null)
                await secretStorage.RemoveAsync(oldSecretKey);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to restore old secret key {Key} during rollback",
                oldSecretKey);
        }
    }

    public async Task RemoveConnectionAsync(Connection connection)
    {
        await _lock.WaitAsync();
        var snapshot = _config.Connections.Select(c => c.Clone()).ToList();
        var chartsSnapshot = _config.ChartsByConnection
            .ToDictionary(kv => kv.Key, kv => new List<ChartConfiguration>(kv.Value));
        var emulatorsSnapshot = _config.EmulatorsByConnection
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Connection? removed = null;
        string? removedSecretValue = null;

        try
        {
            var existing = _config.Connections.FindIndex(c => c.Id == connection.Id);
            if (existing >= 0)
            {
                removed = _config.Connections[existing];
                _config.Connections.RemoveAt(existing);
                _config.ChartsByConnection.Remove(removed.Id);
                _config.EmulatorsByConnection.Remove(removed.Id);
                if (_secretStorage != null)
                {
                    removedSecretValue = await _secretStorage.GetAsync(SecretKey(removed));
                    await _secretStorage.RemoveAsync(SecretKey(removed));
                }
            }

            await SaveCoreAsync();
        }
        catch (Exception)
        {
            _config.Connections = snapshot;
            _config.ChartsByConnection = chartsSnapshot;
            _config.EmulatorsByConnection = emulatorsSnapshot;

            if (_secretStorage != null && removed is not null)
            {
                try
                {
                    if (removedSecretValue is not null)
                        await _secretStorage.SetAsync(SecretKey(removed), removedSecretValue);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to restore secret for removed connection {Name} during rollback",
                        removed.Name);
                }
            }
            throw;
        }
        finally
        {
            _lock.Release();
        }

        // After successful persistence, delete the associated cert asset (best-effort)
        if (removed?.ClientCertificateAssetId is not null && _certStore is not null)
        {
            try { await _certStore.DeleteAsync(removed.Id, removed.ClientCertificateAssetId); } catch { /* config already persisted; the orphan sweep at next startup deletes it */ }
        }
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

    // Seeded on first run so a user without their own broker can try the app immediately.
    // ClientId is left to auto-generate per install to avoid collisions on these shared public brokers.
    private static List<Connection> CreateDefaultConnections() =>
    [
        new()
        {
            Name = "HiveMQ — TCP 1883",
            Host = "broker.hivemq.com",
            Port = 1883,
            Protocol = Protocol.Mqtt,
            UseTls = false,
            SubscribedTopics = [new SubscribedTopic { Topic = "spBv1.0/#" }]
        },
        new()
        {
            Name = "EMQX — TLS 8883",
            Host = "broker.emqx.io",
            Port = 8883,
            Protocol = Protocol.Mqtt,
            UseTls = true,
            AllowUntrustedCertificate = false,
            SubscribedTopics = [new SubscribedTopic { Topic = "spBv1.0/#" }]
        },
        new()
        {
            Name = "Mosquitto — WebSocket TLS 8081",
            Host = "test.mosquitto.org",
            Port = 8081,
            Protocol = Protocol.WebSocket,
            UseTls = true,
            AllowUntrustedCertificate = false,
            WebsocketBasePath = "mqtt",
            SubscribedTopics = [new SubscribedTopic { Topic = "spBv1.0/#" }]
        }
    ];

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

    private bool _disposed;

    // CA1001: the type owns a SemaphoreSlim. These are app-lifetime singletons, so this
    // only runs at container teardown, but leaving the handle undisposed is still a leak.
    // Full Dispose(bool) pattern because the type is not sealed (S3881).
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _lock.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

}
