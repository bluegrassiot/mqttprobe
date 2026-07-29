using Microsoft.Extensions.Logging;

namespace MqttProbe.Services.Plugins.Packaging;

public static class PluginPendingOperations
{
    public const string RemoveMarkerExtension = ".remove";

    // Latest request wins: staging an upgrade or a removal first clears whatever was
    // already pending for that id, so at most one operation per id is outstanding.
    public static string PrepareStagedUpgradePath(string pluginFolder, string id, ILogger? logger = null)
    {
        var pendingRoot = Path.Combine(pluginFolder, PluginPackagePaths.PendingFolderName);
        Directory.CreateDirectory(pendingRoot);

        ClearStagedUpgrade(pendingRoot, id, logger);

        var marker = Path.Combine(pendingRoot, id + RemoveMarkerExtension);

        if (File.Exists(marker))
        {
            File.Delete(marker);
        }

        return Path.Combine(pendingRoot, id);
    }

    public static void MarkForRemoval(string pluginFolder, string id, ILogger? logger = null)
    {
        var pendingRoot = Path.Combine(pluginFolder, PluginPackagePaths.PendingFolderName);
        Directory.CreateDirectory(pendingRoot);

        ClearStagedUpgrade(pendingRoot, id, logger);

        File.WriteAllText(Path.Combine(pendingRoot, id + RemoveMarkerExtension), string.Empty);
    }

    // Renaming aside tolerates a locked file inside the staged directory; deleting in
    // place does not, and a delete that fails partway leaves a half-complete upgrade.
    private static void ClearStagedUpgrade(string pendingRoot, string id, ILogger? logger)
    {
        var stagedPath = Path.Combine(pendingRoot, id);

        if (!Directory.Exists(stagedPath))
        {
            return;
        }

        var asidePath = Path.Combine(pendingRoot, PluginPackagePaths.BackupDirectoryName(stagedPath));

        try
        {
            Directory.Move(stagedPath, asidePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex,
                "Could not clear the previously staged plugin upgrade for {Id}; it will be retried on next start.", id);
            return;
        }

        try
        {
            Directory.Delete(asidePath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not remove stale staged plugin directory for {Id}; remove it manually.", id);
        }
    }

    public static bool HasPendingOperation(string pluginFolder, string id)
    {
        var pendingRoot = Path.Combine(pluginFolder, PluginPackagePaths.PendingFolderName);

        return File.Exists(Path.Combine(pendingRoot, id + RemoveMarkerExtension))
            || Directory.Exists(Path.Combine(pendingRoot, id));
    }

    public static bool RemoveNow(string installPath)
    {
        try
        {
            if (Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Runs at startup before any loader, so nothing holds a plugin DLL open.
    public static IReadOnlyList<string> Apply(IEnumerable<string> pluginFolders, ILogger? logger)
    {
        var applied = new List<string>();

        foreach (var pluginFolder in pluginFolders)
        {
            var pendingRoot = Path.Combine(pluginFolder, PluginPackagePaths.PendingFolderName);

            if (!Directory.Exists(pendingRoot))
            {
                continue;
            }

            foreach (var marker in Directory.GetFiles(pendingRoot, "*" + RemoveMarkerExtension))
            {
                var id = Path.GetFileNameWithoutExtension(marker);

                if (TryApply(() =>
                    {
                        DeleteInstall(pluginFolder, id);
                        File.Delete(marker);
                    },
                    logger, "remove", id))
                {
                    applied.Add(id);
                }
            }

            foreach (var staged in Directory.GetDirectories(pendingRoot))
            {
                var id = Path.GetFileName(staged);

                if (PluginPackagePaths.IsReservedDirectoryName(id))
                {
                    continue;
                }

                if (TryApply(() =>
                    {
                        var installPath = DeleteInstall(pluginFolder, id);
                        Directory.Move(staged, installPath);
                    },
                    logger, "upgrade", id))
                {
                    applied.Add(id);
                }
            }
        }

        return applied;
    }

    private static string DeleteInstall(string pluginFolder, string id)
    {
        var installPath = Path.Combine(pluginFolder, id);

        if (Directory.Exists(installPath))
        {
            Directory.Delete(installPath, recursive: true);
        }

        return installPath;
    }

    // A marker that cannot be applied is left in place and retried next start
    // rather than silently dropped, so a transient lock never loses the request.
    private static bool TryApply(Action action, ILogger? logger, string operation, string id)
    {
        try
        {
            action();
            if (logger != null && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Applied pending plugin {Operation} for {Id}.", operation, id);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex,
                "Could not apply pending plugin {Operation} for {Id}; it will be retried on next start.",
                operation, id);
            return false;
        }
    }
}
