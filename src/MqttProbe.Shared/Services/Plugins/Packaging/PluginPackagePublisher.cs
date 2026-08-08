using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;

namespace MqttProbe.Services.Plugins.Packaging;

internal sealed class PluginPackagePublisher(
    PluginPackageArchiveReader reader,
    PluginInstallSession session,
    ILogger logger)
{
    public async Task<PluginInstallOutcome> InstallAsync(
        Stream package, string pluginFolder, CancellationToken ct)
    {
        string? archivePath = null;
        string? extractPath = null;

        try
        {
            var stagingRoot = Path.Combine(pluginFolder, PluginPackagePaths.StagingFolderName);
            Directory.CreateDirectory(stagingRoot);

            var token = Guid.NewGuid().ToString("N");
            archivePath = Path.Combine(stagingRoot, token + ".zip");
            extractPath = Path.Combine(stagingRoot, token);

            var readResult = await reader.ReadAsync(package, archivePath, extractPath, ct);

            if (readResult.Failure is not null)
            {
                return readResult.Failure;
            }

            var manifest = readResult.Manifest!;
            var installPath = PluginPackagePaths.ResolveInstallPath(
                pluginFolder, manifest.Kind, manifest.Id);

            ApplyExtractedPackage(pluginFolder, manifest, readResult.ExtractPath, installPath);

            var requiresRestart = manifest.Kind == PluginPackageKinds.Assembly;
            session.Record(manifest.Id, requiresRestart);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Installed plugin package {Id} {Version} to {Path}.",
                    manifest.Id, manifest.Version, installPath);
            }

            return PluginInstallOutcome.Success(manifest, installPath, requiresRestart);
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

    private void ApplyExtractedPackage(
        string pluginFolder,
        PluginPackageManifest manifest,
        string extractPath,
        string installPath)
    {
        var deferUpgrade = manifest.Kind == PluginPackageKinds.Assembly && Directory.Exists(installPath);

        if (deferUpgrade)
        {
            var pendingPath = PluginPendingOperations.PrepareStagedUpgradePath(
                pluginFolder, manifest.Id, logger);
            Swap(extractPath, pendingPath);
        }
        else
        {
            Swap(extractPath, installPath);
        }
    }

    internal void Swap(string stagedPath, string installPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);

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
                    logger.LogError(
                        restoreEx,
                        "Failed to restore the previous plugin install from backup {BackupPath} to {InstallPath} " +
                        "after a failed upgrade. Manual recovery is required.",
                        backupPath,
                        installPath);
                }
            }

            throw;
        }

        if (hadPrevious && !TryDeleteDirectory(backupPath))
        {
            logger.LogWarning(
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
            // Best-effort cleanup is retried by the next install sweep.
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
