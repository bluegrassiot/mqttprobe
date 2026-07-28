using System.Reflection;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Contracts;
using MqttProbe.Services.Plugins.Packaging;

namespace MqttProbe.Services.Plugins.Loading;

public sealed class PluginLoader
{
    private readonly PluginConfig _config;
    private readonly ILogger<PluginLoader> _logger;

    public PluginLoader(PluginConfig config, ILogger<PluginLoader> logger)
    {
        _config = config;
        _logger = logger;
    }

    public PluginLoadResult LoadPlugins()
    {
        var plugins = new List<IMqttProbePlugin>();
        var diagnostics = new List<PluginDiagnosticEntry>();
        var loadedPaths = new List<string>();
        var seenDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _config.PluginFolders)
        {
            if (!Directory.Exists(folder))
            {
                _logger.LogWarning("Plugin folder not found: {Folder}", folder);
                diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = "loader",
                    Severity = DiagnosticSeverity.Info,
                    Message = $"Plugin folder not found: {folder}",
                    SourcePath = folder
                });
                continue;
            }

            var dlls = DiscoverPluginDlls(folder);

            if (dlls.Count == 0)
            {
                diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = "loader",
                    Severity = DiagnosticSeverity.Info,
                    Message = $"No DLLs found in plugin folder: {folder}",
                    SourcePath = folder
                });
                continue;
            }

            foreach (var dll in dlls)
            {
                if (!seenDlls.Add(dll))
                    continue;

                LoadAssemblyPlugins(dll, plugins, diagnostics, loadedPaths);
            }
        }

        return new PluginLoadResult(plugins.AsReadOnly(), diagnostics.AsReadOnly(), loadedPaths.AsReadOnly());
    }

    private static List<string> DiscoverPluginDlls(string folder)
    {
        var dlls = new List<string>();

        dlls.AddRange(Directory.GetFiles(folder, "*.dll"));

        foreach (var sub in Directory.GetDirectories(folder))
        {
            var subName = Path.GetFileName(sub);

            // Skips every dot-prefixed directory: .staging/.pending hold half-written
            // packages mid-install, and .*.previous-<guid> are leaked upgrade backups.
            if (PluginPackagePaths.IsReservedDirectoryName(subName))
            {
                continue;
            }

            var preferred = Path.Combine(sub, subName + ".dll");
            if (File.Exists(preferred))
            {
                dlls.Add(preferred);
            }
            else
            {
                dlls.AddRange(Directory.GetFiles(sub, "*.dll")
                    .Where(f => !f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return dlls;
    }

    private void LoadAssemblyPlugins(
        string dllPath,
        List<IMqttProbePlugin> plugins,
        List<PluginDiagnosticEntry> diagnostics,
        List<string> loadedPaths)
    {
        PluginLoadContext? loadContext = null;
        Assembly? assembly = null;

        try
        {
            loadContext = new PluginLoadContext(dllPath);
            assembly = loadContext.LoadFromAssemblyPath(dllPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load assembly: {Path}", dllPath);
            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = "loader",
                Severity = DiagnosticSeverity.Warning,
                Message = $"Failed to load assembly: {Path.GetFileName(dllPath)}",
                Details = ex.Message,
                SourcePath = dllPath
            });
            loadContext?.Unload();
            return;
        }

        var exportedTypes = TryGetExportedTypes(assembly, loadContext, dllPath, diagnostics);

        if (exportedTypes is null)
        {
            return;
        }

        var pluginTypes = exportedTypes
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                && typeof(IMqttProbePlugin).IsAssignableFrom(t))
            .ToList();

        if (pluginTypes.Count == 0)
        {
            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = "loader",
                Severity = DiagnosticSeverity.Info,
                Message = $"No IMqttProbePlugin implementations in: {Path.GetFileName(dllPath)}",
                SourcePath = dllPath
            });
            loadContext.Unload();
            return;
        }

        foreach (var pluginType in pluginTypes)
        {
            TryLoadPluginType(pluginType, dllPath, plugins, diagnostics, loadedPaths);
        }
    }

    private Type[]? TryGetExportedTypes(
        Assembly assembly, PluginLoadContext loadContext, string dllPath, List<PluginDiagnosticEntry> diagnostics)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var exportedTypes = ex.Types.Where(t => t != null).ToArray()!;
            if (exportedTypes.Length == 0)
            {
                diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = "loader",
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"No loadable types in assembly: {Path.GetFileName(dllPath)}",
                    Details = string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message)),
                    SourcePath = dllPath
                });
                loadContext.Unload();
                return null;
            }

            return exportedTypes;
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = "loader",
                Severity = DiagnosticSeverity.Warning,
                Message = $"Failed to enumerate types in assembly: {Path.GetFileName(dllPath)}",
                Details = ex.Message,
                SourcePath = dllPath
            });
            loadContext.Unload();
            return null;
        }
    }

    private void TryLoadPluginType(
        Type pluginType,
        string dllPath,
        List<IMqttProbePlugin> plugins,
        List<PluginDiagnosticEntry> diagnostics,
        List<string> loadedPaths)
    {
        IMqttProbePlugin? plugin = null;

        try
        {
            plugin = (IMqttProbePlugin?)Activator.CreateInstance(pluginType);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = pluginType.FullName ?? pluginType.Name,
                Severity = DiagnosticSeverity.Error,
                Message = $"Failed to instantiate plugin type: {pluginType.Name}",
                Details = ex.Message,
                SourcePath = dllPath
            });
            return;
        }

        if (plugin is null)
        {
            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = pluginType.FullName ?? pluginType.Name,
                Severity = DiagnosticSeverity.Error,
                Message = $"Plugin type returned null instance: {pluginType.Name}",
                SourcePath = dllPath
            });
            return;
        }

        // Disabled check happens after instantiation because PluginId is an
        // instance property.  The alternative (an attribute-based pre-check)
        // would couple the loader to a convention outside the interface.
        if (_config.DisabledPluginIds.Contains(plugin.PluginId))
        {
            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = plugin.PluginId,
                Severity = DiagnosticSeverity.Info,
                Message = $"Plugin '{plugin.PluginId}' is disabled; skipped.",
                SourcePath = dllPath
            });
            return;
        }

        plugins.Add(plugin);
        loadedPaths.Add(Path.GetDirectoryName(dllPath)!);
        _logger.LogDebug("Loaded plugin: {PluginId} from {Assembly}",
            plugin.PluginId, Path.GetFileName(dllPath));
    }
}

public sealed class PluginLoadResult
{
    public IReadOnlyList<IMqttProbePlugin> Plugins { get; }
    public IReadOnlyList<PluginDiagnosticEntry> Diagnostics { get; }
    public IReadOnlyList<string> LoadedPaths { get; }

    public PluginLoadResult(
        IReadOnlyList<IMqttProbePlugin> plugins,
        IReadOnlyList<PluginDiagnosticEntry> diagnostics,
        IReadOnlyList<string> loadedPaths)
    {
        Plugins = plugins;
        Diagnostics = diagnostics;
        LoadedPaths = loadedPaths;
    }
}
