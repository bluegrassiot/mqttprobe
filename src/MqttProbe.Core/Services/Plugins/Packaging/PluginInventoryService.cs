using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.Core.Services.Plugins.Registry;

namespace MqttProbe.Core.Services.Plugins.Packaging;

public sealed class PluginInventoryService(
    PluginConfig config, PayloadPipeline pipeline, PluginInstallSession session)
{
    private readonly PluginConfig _config = config;
    private readonly PayloadPipeline _pipeline = pipeline;
    private readonly PluginInstallSession _session = session;

    public IReadOnlyList<InstalledPlugin> GetInstalled()
    {
        var registry = _pipeline.Registry;
        var results = new List<InstalledPlugin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pluginFolder in _config.PluginFolders)
        {
            foreach (var candidate in CandidateDirectories(pluginFolder))
            {
                var manifestPath = Path.Combine(candidate, PluginManifestValidator.FileName);

                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                PluginPackageManifest? manifest;

                try
                {
                    manifest = PluginManifestValidator.Deserialize(File.ReadAllText(manifestPath));
                }
                catch (IOException)
                {
                    continue;
                }

                if (manifest is null || !seen.Add(manifest.Id))
                {
                    continue;
                }

                var installPath = Path.GetFullPath(candidate);
                var (status, detail) = DeriveStatus(manifest, installPath, pluginFolder, registry);

                results.Add(new InstalledPlugin(manifest, installPath, status, detail));
            }
        }

        return results
            .OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> CandidateDirectories(string pluginFolder)
    {
        if (!Directory.Exists(pluginFolder))
        {
            yield break;
        }

        foreach (var directory in Directory.GetDirectories(pluginFolder))
        {
            if (PluginPackagePaths.IsReservedDirectoryName(Path.GetFileName(directory)))
            {
                continue;
            }

            yield return directory;
        }

        var protobufFolder = Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName);

        if (!Directory.Exists(protobufFolder))
        {
            yield break;
        }

        foreach (var directory in Directory.GetDirectories(protobufFolder))
        {
            if (PluginPackagePaths.IsReservedDirectoryName(Path.GetFileName(directory)))
            {
                continue;
            }

            yield return directory;
        }
    }

    private (PluginStatus Status, string? Detail) DeriveStatus(
        PluginPackageManifest manifest,
        string installPath,
        string pluginFolder,
        PluginRegistry registry)
    {
        if (PluginPendingOperations.HasPendingOperation(pluginFolder, manifest.Id))
        {
            return (PluginStatus.PendingRestart, "Applied on next restart.");
        }

        if (_session.TryGet(manifest.Id, out var requiresRestart))
        {
            return requiresRestart
                ? (PluginStatus.PendingRestart, "Restart MQTTProbe to load this plugin.")
                : (PluginStatus.PendingActivation, "Apply to activate without restarting.");
        }

        if (_config.DisabledPluginIds.Contains(manifest.Id))
        {
            return (PluginStatus.Disabled, "Disabled by configuration.");
        }

        if (registry.LoadedPackagePaths.Contains(installPath))
        {
            return (PluginStatus.Active, null);
        }

        var diagnostic = registry.Diagnostics.FirstOrDefault(d =>
            d.Severity >= DiagnosticSeverity.Warning
            && d.SourcePath is not null
            && IsWithin(installPath, d.SourcePath));

        return diagnostic is null
            ? (PluginStatus.Failed, "Installed but not loaded.")
            : (PluginStatus.Failed, $"{diagnostic.Message} {diagnostic.Details}".Trim());
    }

    // installPath is already Path.GetFullPath-normalised by the caller; the diagnostic's
    // SourcePath must be normalised the same way here, or a loaded package's own error
    // diagnostic would never match and mask a genuine Active/Failed distinction.
    private static bool IsWithin(string installPath, string candidate)
    {
        var full = Path.GetFullPath(candidate);

        return full.Equals(installPath, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(installPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
