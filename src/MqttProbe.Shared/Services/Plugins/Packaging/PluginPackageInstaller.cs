using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Services.Plugins.Packaging;

public sealed class PluginPackageInstaller
{
    private readonly PluginConfig _config;
    private readonly IAppInfoService _appInfo;
    private readonly PluginInstallSession _session;
    private readonly PluginArchiveLimits _limits;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginPackageInstaller> _logger;

    private static readonly JsonSerializerOptions _schemaManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public PluginPackageInstaller(
        PluginConfig config,
        IAppInfoService appInfo,
        PluginInstallSession session,
        PluginArchiveLimits limits,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _appInfo = appInfo;
        _session = session;
        _limits = limits;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginPackageInstaller>();
    }

    public string? WritablePluginFolder
    {
        get
        {
            foreach (var folder in _config.PluginFolders)
            {
                if (IsWritable(folder))
                {
                    return folder;
                }
            }

            return null;
        }
    }

    // Test-only seam: real filesystem permission checks behave inconsistently across CI
    // environments (a root-owned runner bypasses them entirely), so RemoveAsync's "owning
    // folder is not writable" branch is exercised through this override instead.
    internal Func<string, bool>? WritableProbeOverride { get; set; }

    private bool IsWritable(string folder) =>
        (WritableProbeOverride ?? DefaultIsWritable)(folder);

    private static bool DefaultIsWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var probe = Path.Combine(folder, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public async Task<PluginInstallOutcome> InstallAsync(Stream package, CancellationToken ct)
    {
        string? archivePath = null;
        string? extractPath = null;

        try
        {
            var pluginFolder = WritablePluginFolder;

            if (pluginFolder is null)
            {
                return PluginInstallOutcome.Fail("No writable plugin folder is configured on this host.");
            }

            var stagingRoot = Path.Combine(pluginFolder, PluginPackagePaths.StagingFolderName);
            Directory.CreateDirectory(stagingRoot);

            var token = Guid.NewGuid().ToString("N");
            archivePath = Path.Combine(stagingRoot, token + ".zip");
            extractPath = Path.Combine(stagingRoot, token);

            var copied = await CopyWithLimitAsync(package, archivePath, ct);

            if (copied is null)
            {
                return PluginInstallOutcome.Fail(
                    $"Package exceeds the maximum upload size of {_limits.MaxCompressedBytes} bytes.");
            }

            return await PublishAsync(pluginFolder, archivePath, extractPath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return PluginInstallOutcome.Fail("The uploaded file is not a readable zip archive.");
        }
        catch (PackageExpansionLimitExceededException ex)
        {
            return PluginInstallOutcome.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin install failed.");
            return PluginInstallOutcome.Fail("Install failed. See the server log for details.");
        }
        finally
        {
            if (archivePath is not null)
            {
                TryDeleteFile(archivePath);
            }

            if (extractPath is not null)
            {
                TryDeleteDirectory(extractPath);
            }
        }
    }

    private async Task<long?> CopyWithLimitAsync(Stream source, string destinationPath, CancellationToken ct)
    {
        await using var destination = File.Create(destinationPath);

        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;

            if (total > _limits.MaxCompressedBytes)
            {
                return null;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return total;
    }

    private async Task<PluginInstallOutcome> PublishAsync(
        string pluginFolder, string archivePath, string extractPath, CancellationToken ct)
    {
        PluginPackageManifest? manifest;

        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var manifestEntry = archive.GetEntry(PluginManifestValidator.FileName);

            if (manifestEntry is null)
            {
                return PluginInstallOutcome.Fail(
                    $"Package does not contain {PluginManifestValidator.FileName} at its root.");
            }

            using var reader = new StreamReader(manifestEntry.Open());
            manifest = PluginManifestValidator.Deserialize(await reader.ReadToEndAsync(ct));

            var manifestResult = PluginManifestValidator.Validate(manifest, _appInfo.GetVersion());

            if (!manifestResult.IsValid)
            {
                return PluginInstallOutcome.Fail(manifestResult.Error!);
            }

            var kindResult = ValidateKindIsPermitted(manifest!.Kind);

            if (!kindResult.IsValid)
            {
                return PluginInstallOutcome.Fail(kindResult.Error!);
            }

            var entryResult = PluginArchiveValidator.ValidateEntries(archive, manifest.Kind, _limits);

            if (!entryResult.IsValid)
            {
                return PluginInstallOutcome.Fail(entryResult.Error!);
            }

            Extract(archive, extractPath, ct);
        }

        var contentResult = ValidateExtractedContent(manifest!, extractPath, ct);

        if (!contentResult.IsValid)
        {
            return PluginInstallOutcome.Fail(contentResult.Error!);
        }

        var installPath = PluginPackagePaths.ResolveInstallPath(pluginFolder, manifest.Kind, manifest.Id);

        // The running process may already have this assembly loaded, which holds
        // its directory open on Windows; stage the upgrade under .pending instead
        // of swapping in place, and let PluginPendingOperations.Apply move it into
        // place at next start, before anything has had a chance to load it again.
        var deferUpgrade = manifest.Kind == PluginPackageKinds.Assembly && Directory.Exists(installPath);

        if (deferUpgrade)
        {
            var pendingPath = PluginPendingOperations.PrepareStagedUpgradePath(pluginFolder, manifest.Id, _logger);

            Swap(extractPath, pendingPath);
        }
        else
        {
            Swap(extractPath, installPath);
        }

        var requiresRestart = manifest.Kind == PluginPackageKinds.Assembly;
        _session.Record(manifest.Id, requiresRestart);

        _logger.LogInformation("Installed plugin package {Id} {Version} to {Path}.",
            manifest.Id, manifest.Version, installPath);

        return PluginInstallOutcome.Success(manifest, installPath, requiresRestart);
    }

    private PluginValidationResult ValidateKindIsPermitted(string kind)
    {
        if (kind != PluginPackageKinds.Assembly)
        {
            return PluginValidationResult.Success;
        }

        if (!_config.AllowBinaryPackages)
        {
            return PluginValidationResult.Fail(
                "Binary plugin packages are not enabled on this instance. Set Plugins:AllowBinaryPackages to true to allow them.");
        }

        if (!PluginAssemblyInspector.AssemblyPluginsSupported)
        {
            return PluginValidationResult.Fail(
                "Binary plugin packages are not supported on this platform. Install a protobuf schema package instead.");
        }

        return PluginValidationResult.Success;
    }

    private PluginValidationResult ValidateExtractedContent(
        PluginPackageManifest manifest, string extractPath, CancellationToken ct)
    {
        if (manifest.Kind == PluginPackageKinds.Assembly)
        {
            return PluginAssemblyInspector.Validate(extractPath, manifest.Id);
        }

        if (manifest.Kind != PluginPackageKinds.ProtobufSchemas)
        {
            return PluginValidationResult.Success;
        }

        ct.ThrowIfCancellationRequested();

        var schemaManifestPath = Path.Combine(extractPath, ProtobufSchemaFolderLoader.ManifestFileName);

        if (!File.Exists(schemaManifestPath))
        {
            return PluginValidationResult.Fail(
                $"Schema package does not contain {ProtobufSchemaFolderLoader.ManifestFileName}.");
        }

        ProtobufSchemaManifest? schemaManifest;

        try
        {
            schemaManifest = JsonSerializer.Deserialize<ProtobufSchemaManifest>(
                File.ReadAllText(schemaManifestPath), _schemaManifestOptions);
        }
        catch (JsonException ex)
        {
            return PluginValidationResult.Fail(
                $"{ProtobufSchemaFolderLoader.ManifestFileName} is not valid JSON: {ex.Message}");
        }

        if (schemaManifest is null || schemaManifest.Schemas.Count == 0)
        {
            return PluginValidationResult.Fail(
                $"{ProtobufSchemaFolderLoader.ManifestFileName} declares no schemas.");
        }

        var registry = new ProtobufSchemaRegistry(
            schemaManifest, extractPath, _loggerFactory.CreateLogger<ProtobufSchemaRegistry>());

        if (!registry.HasAnySchemas)
        {
            return PluginValidationResult.Fail(
                $"No schemas compiled from this package. {string.Join(" ", registry.Diagnostics)}".Trim());
        }

        foreach (var mapping in schemaManifest.Schemas)
        {
            ct.ThrowIfCancellationRequested();

            if (!registry.TryResolveMessage(mapping.MessageType, out _))
            {
                return PluginValidationResult.Fail(
                    $"Declared message type '{mapping.MessageType}' was not found in the package's .proto files.");
            }
        }

        return PluginValidationResult.Success;
    }

    // Distinguishes the expansion cap from other extraction failures so InstallAsync
    // can surface its specific message instead of the generic server-log fallback.
    private sealed class PackageExpansionLimitExceededException : Exception
    {
        public PackageExpansionLimitExceededException(string message) : base(message)
        {
        }
    }

    private void Extract(ZipArchive archive, string extractPath, CancellationToken ct)
    {
        Directory.CreateDirectory(extractPath);

        var buffer = new byte[81920];
        long totalUncompressed = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!PluginArchiveValidator.TryResolveDestination(extractPath, entry.FullName, out var destination))
            {
                throw new InvalidOperationException($"Unsafe entry path: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Declared Length/CompressedLength are attacker-controlled central-directory
            // values; ZipArchiveEntry.Open() does not truncate at them, so the only
            // reliable cap is counting bytes as they are actually written to disk.
            using var source = entry.Open();
            using var target = File.Create(destination);

            int read;

            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                totalUncompressed += read;

                if (totalUncompressed > _limits.MaxUncompressedBytes)
                {
                    throw new PackageExpansionLimitExceededException(
                        $"Package expands to more than the permitted {_limits.MaxUncompressedBytes} uncompressed bytes.");
                }

                target.Write(buffer, 0, read);
            }
        }
    }

    public Task<PluginInstallOutcome> RemoveAsync(string id, CancellationToken ct)
    {
        _ = ct;

        var pluginFolder = WritablePluginFolder;

        if (pluginFolder is null)
        {
            return Task.FromResult(
                PluginInstallOutcome.Fail("No writable plugin folder is configured on this host."));
        }

        var schemaPath = PluginPackagePaths.ResolveInstallPath(
            pluginFolder, PluginPackageKinds.ProtobufSchemas, id);

        if (Directory.Exists(schemaPath))
        {
            if (!PluginPendingOperations.RemoveNow(schemaPath))
            {
                return Task.FromResult(
                    PluginInstallOutcome.Fail($"Could not delete {schemaPath}."));
            }

            _session.Record(id, requiresRestart: false);
            return Task.FromResult(new PluginInstallOutcome(true, null, null, schemaPath, false));
        }

        var assemblyPath = PluginPackagePaths.ResolveInstallPath(
            pluginFolder, PluginPackageKinds.Assembly, id);

        if (!Directory.Exists(assemblyPath))
        {
            return Task.FromResult(PluginInstallOutcome.Fail($"Plugin '{id}' is not installed."));
        }

        // The running process holds this DLL open, so deletion has to wait for restart.
        try
        {
            PluginPendingOperations.MarkForRemoval(pluginFolder, id, _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not mark plugin {Id} for removal; it will be retried on next start.", id);
            return Task.FromResult(
                PluginInstallOutcome.Fail($"Could not remove plugin '{id}'. It will be retried on next start."));
        }

        _session.Record(id, requiresRestart: true);

        return Task.FromResult(new PluginInstallOutcome(true, null, null, assemblyPath, true));
    }

    // Used when the caller already knows which physical directory backs the package (as
    // PluginInventoryService's InstallPath does): resolving against WritablePluginFolder alone
    // picks only the *first* writable entry in PluginFolders, which is wrong whenever the
    // package actually lives under a later one.
    public Task<PluginInstallOutcome> RemoveAsync(string id, string installPath, CancellationToken ct)
    {
        _ = ct;

        var resolvedInstallPath = Path.GetFullPath(installPath);

        foreach (var pluginFolder in _config.PluginFolders)
        {
            var schemaPath = PluginPackagePaths.ResolveInstallPath(pluginFolder, PluginPackageKinds.ProtobufSchemas, id);

            if (PathsEqual(schemaPath, resolvedInstallPath))
            {
                if (!Directory.Exists(resolvedInstallPath))
                {
                    return Task.FromResult(PluginInstallOutcome.Fail($"Plugin '{id}' is not installed."));
                }

                if (!PluginPendingOperations.RemoveNow(resolvedInstallPath))
                {
                    return Task.FromResult(PluginInstallOutcome.Fail($"Could not delete {resolvedInstallPath}."));
                }

                _session.Record(id, requiresRestart: false);
                return Task.FromResult(new PluginInstallOutcome(true, null, null, resolvedInstallPath, false));
            }

            var assemblyPath = PluginPackagePaths.ResolveInstallPath(pluginFolder, PluginPackageKinds.Assembly, id);

            if (PathsEqual(assemblyPath, resolvedInstallPath))
            {
                if (!Directory.Exists(resolvedInstallPath))
                {
                    return Task.FromResult(PluginInstallOutcome.Fail($"Plugin '{id}' is not installed."));
                }

                if (!IsWritable(pluginFolder))
                {
                    return Task.FromResult(PluginInstallOutcome.Fail(
                        $"Plugin '{id}' is installed in a folder that is not writable on this host; it cannot be removed."));
                }

                // The running process holds this DLL open, so deletion has to wait for restart.
                try
                {
                    PluginPendingOperations.MarkForRemoval(pluginFolder, id, _logger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not mark plugin {Id} for removal; it will be retried on next start.", id);
                    return Task.FromResult(
                        PluginInstallOutcome.Fail($"Could not remove plugin '{id}'. It will be retried on next start."));
                }

                _session.Record(id, requiresRestart: true);
                return Task.FromResult(new PluginInstallOutcome(true, null, null, resolvedInstallPath, true));
            }
        }

        return Task.FromResult(PluginInstallOutcome.Fail($"Plugin '{id}' is not installed."));
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    internal void Swap(string stagedPath, string installPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);

        // Dot-prefixed so both PluginLoader and ProtobufSchemaFolderLoader's
        // reserved-name skip hides this backup if the cleanup below leaks it,
        // instead of it being discovered as a second, stale live package.
        var backupPath = Path.Combine(
            Path.GetDirectoryName(installPath)!,
            PluginPackagePaths.BackupDirectoryName(installPath));

        var hadPrevious = Directory.Exists(installPath);

        if (hadPrevious)
        {
            Directory.Move(installPath, backupPath);
        }

        try
        {
            Directory.Move(stagedPath, installPath);
        }
        catch
        {
            if (hadPrevious)
            {
                try
                {
                    Directory.Move(backupPath, installPath);
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx,
                        "Failed to restore the previous plugin install from backup {BackupPath} to {InstallPath} " +
                        "after a failed upgrade. Manual recovery is required.",
                        backupPath, installPath);
                }
            }

            throw;
        }

        if (hadPrevious && !TryDeleteDirectory(backupPath))
        {
            _logger.LogWarning(
                "Failed to remove upgrade backup {BackupPath}; remove it manually.", backupPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
