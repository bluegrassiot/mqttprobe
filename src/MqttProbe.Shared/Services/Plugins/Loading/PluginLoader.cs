using System.Reflection;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Contracts;

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
        var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _config.PluginFolders)
        {
            if (!Directory.Exists(folder))
            {
                _logger.LogWarning("Plugin folder not found: {Folder}", folder);
                diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = "loader",
                    Severity = DiagnosticSeverity.Info,
                    Message = $"Plugin folder not found: {folder}"
                });
                continue;
            }

            var dlls = new List<string>();

            dlls.AddRange(Directory.GetFiles(folder, "*.dll"));

            foreach (var sub in Directory.GetDirectories(folder))
            {
                var subName = Path.GetFileName(sub);
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

            if (dlls.Count == 0)
            {
                diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = "loader",
                    Severity = DiagnosticSeverity.Info,
                    Message = $"No DLLs found in plugin folder: {folder}"
                });
                continue;
            }

            foreach (var dll in dlls)
            {
                if (!loadedPaths.Add(dll))
                    continue;

                LoadAssemblyPlugins(dll, plugins, diagnostics);
            }
        }

        return new PluginLoadResult(plugins.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private void LoadAssemblyPlugins(
        string dllPath,
        List<IMqttProbePlugin> plugins,
        List<PluginDiagnosticEntry> diagnostics)
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
                Details = ex.Message
            });
            loadContext?.Unload();
            return;
        }

        Type[] exportedTypes;
        try
        {
            exportedTypes = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            exportedTypes = ex.Types.Where(t => t != null).ToArray()!;
            if (exportedTypes.Length == 0)
            {
                diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = "loader",
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"No loadable types in assembly: {Path.GetFileName(dllPath)}",
                    Details = string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message))
                });
                loadContext.Unload();
                return;
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnosticEntry
            {
                Source = "loader",
                Severity = DiagnosticSeverity.Warning,
                Message = $"Failed to enumerate types in assembly: {Path.GetFileName(dllPath)}",
                Details = ex.Message
            });
            loadContext.Unload();
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
                Message = $"No IMqttProbePlugin implementations in: {Path.GetFileName(dllPath)}"
            });
            loadContext.Unload();
            return;
        }

        foreach (var pluginType in pluginTypes)
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
                    Details = ex.Message
                });
                continue;
            }

            if (plugin is null)
            {
                diagnostics.Add(new PluginDiagnosticEntry
                {
                    Source = pluginType.FullName ?? pluginType.Name,
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Plugin type returned null instance: {pluginType.Name}"
                });
                continue;
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
                    Message = $"Plugin '{plugin.PluginId}' is disabled; skipped."
                });
                continue;
            }

            plugins.Add(plugin);
            _logger.LogDebug("Loaded plugin: {PluginId} from {Assembly}",
                plugin.PluginId, Path.GetFileName(dllPath));
        }
    }
}

public sealed class PluginLoadResult
{
    public IReadOnlyList<IMqttProbePlugin> Plugins { get; }
    public IReadOnlyList<PluginDiagnosticEntry> Diagnostics { get; }

    public PluginLoadResult(
        IReadOnlyList<IMqttProbePlugin> plugins,
        IReadOnlyList<PluginDiagnosticEntry> diagnostics)
    {
        Plugins = plugins;
        Diagnostics = diagnostics;
    }
}
