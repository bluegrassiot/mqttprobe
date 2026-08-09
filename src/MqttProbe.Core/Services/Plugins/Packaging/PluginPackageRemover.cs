using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Plugins;

namespace MqttProbe.Core.Services.Plugins.Packaging;

internal sealed class PluginPackageRemover(
    PluginConfig config,
    PluginInstallSession session,
    ILogger logger,
    Func<string, bool> isWritable)
{
    private readonly ILogger? _logger = logger;

    public Task<PluginInstallOutcome> RemoveAsync(string id, CancellationToken ct)
    {
        _ = ct;

        var pluginFolder = config.PluginFolders.FirstOrDefault(isWritable);

        if (pluginFolder is null)
        {
            return Task.FromResult(
                PluginInstallOutcome.Fail("No writable plugin folder is configured on this host."));
        }

        var schemaPath = PluginPackagePaths.ResolveInstallPath(
            pluginFolder, PluginPackageKinds.ProtobufSchemas, id);

        if (Directory.Exists(schemaPath))
        {
            return Task.FromResult(RemoveSchemaPackage(id, schemaPath));
        }

        var assemblyPath = PluginPackagePaths.ResolveInstallPath(
            pluginFolder, PluginPackageKinds.Assembly, id);

        if (!Directory.Exists(assemblyPath))
        {
            return Task.FromResult(PluginInstallOutcome.Fail($"Plugin '{id}' is not installed."));
        }

        return Task.FromResult(
            RemoveAssemblyPackage(
                pluginFolder, id, assemblyPath, isLoaded: true, owningFolderAlreadyProbed: true));
    }

    public Task<PluginInstallOutcome> RemoveAsync(
        string id, string installPath, bool isLoaded, CancellationToken ct)
    {
        _ = ct;

        var resolvedInstallPath = Path.GetFullPath(installPath);

        foreach (var pluginFolder in config.PluginFolders)
        {
            var outcome = TryRemoveFromFolder(pluginFolder, id, resolvedInstallPath, isLoaded);

            if (outcome is not null)
            {
                return Task.FromResult(outcome);
            }
        }

        return Task.FromResult(PluginInstallOutcome.Fail($"Plugin '{id}' is not installed."));
    }

    private PluginInstallOutcome? TryRemoveFromFolder(
        string pluginFolder, string id, string resolvedInstallPath, bool isLoaded)
    {
        var schemaPath = PluginPackagePaths.ResolveInstallPath(
            pluginFolder, PluginPackageKinds.ProtobufSchemas, id);
        var normalizedSchemaPath = Path.GetFullPath(schemaPath);

        if (PathsEqual(normalizedSchemaPath, resolvedInstallPath))
        {
            return RemoveSchemaPackage(id, normalizedSchemaPath);
        }

        var assemblyPath = PluginPackagePaths.ResolveInstallPath(
            pluginFolder, PluginPackageKinds.Assembly, id);
        var normalizedAssemblyPath = Path.GetFullPath(assemblyPath);

        if (PathsEqual(normalizedAssemblyPath, resolvedInstallPath))
        {
            return RemoveAssemblyPackage(
                pluginFolder, id, normalizedAssemblyPath, isLoaded, owningFolderAlreadyProbed: false);
        }

        return null;
    }

    private PluginInstallOutcome RemoveSchemaPackage(string id, string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            return PluginInstallOutcome.Fail($"Plugin '{id}' is not installed.");
        }

        if (!PluginPendingOperations.RemoveNow(installPath))
        {
            return PluginInstallOutcome.Fail($"Could not delete {installPath}.");
        }

        session.Record(id, requiresRestart: false);
        return new PluginInstallOutcome(true, null, null, installPath, false);
    }

    private PluginInstallOutcome RemoveAssemblyPackage(
        string pluginFolder,
        string id,
        string installPath,
        bool isLoaded,
        bool owningFolderAlreadyProbed)
    {
        if (!Directory.Exists(installPath))
        {
            return PluginInstallOutcome.Fail($"Plugin '{id}' is not installed.");
        }

        if (!owningFolderAlreadyProbed && !isWritable(pluginFolder))
        {
            return PluginInstallOutcome.Fail(
                $"Plugin '{id}' is installed in a folder that is not writable on this host; it cannot be removed.");
        }

        if (!isLoaded && PluginPendingOperations.RemoveNow(installPath))
        {
            session.Forget(id);
            return new PluginInstallOutcome(true, null, null, installPath, false);
        }

        try
        {
            PluginPendingOperations.MarkForRemoval(pluginFolder, id, _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                ex, "Could not mark plugin {Id} for removal; it will be retried on next start.", id);
            return PluginInstallOutcome.Fail(
                $"Could not remove plugin '{id}'. It will be retried on next start.");
        }

        session.Record(id, requiresRestart: true);
        return new PluginInstallOutcome(true, null, null, installPath, true);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
