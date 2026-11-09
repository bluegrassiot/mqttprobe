using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Loading;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Services.Plugins;

public static class MqttProbePluginStartup
{
    public static PluginRegistry BuildPluginRegistry(
        PluginConfig config,
        ILoggerFactory loggerFactory,
        PluginAssemblyCache? assemblyCache = null) =>
        BuildPluginRegistry(config, loggerFactory, assemblyCache, combineProtobufSources: null);

    // Test seam: no real schema content fails only once combined (duplicates just warn),
    // so tests inject a failing combiner to reach the combine-failure branch.
    internal static PluginRegistry BuildPluginRegistry(
        PluginConfig config,
        ILoggerFactory loggerFactory,
        PluginAssemblyCache? assemblyCache,
        Func<IReadOnlyList<ProtobufSchemaSource>, ILogger, ProtobufSchemaRegistry>? combineProtobufSources)
    {
        combineProtobufSources ??= static (sources, logger) => new ProtobufSchemaRegistry(sources, logger);

        loggerFactory.CreateLogger(typeof(MqttProbePluginStartup).FullName!)
            .LogInformation("Plugin contract version {Version}", PluginContract.Version);

        var registryBuilder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(registryBuilder);

        var protobufLogger = loggerFactory.CreateLogger<ProtobufSchemaRegistry>();
        var usableSources = DiscoverUsableProtobufSources(config, protobufLogger, registryBuilder);

        if (usableSources.Count > 0)
        {
            CombineAndRegisterProtobufSources(usableSources, protobufLogger, combineProtobufSources, registryBuilder);
        }

        LoadAssemblyPlugins(config, loggerFactory, assemblyCache, registryBuilder);

        return registryBuilder.Build(config.DisabledPluginIds, config.Overrides);
    }

    private static List<ProtobufSchemaSource> DiscoverUsableProtobufSources(
        PluginConfig config,
        ILogger protobufLogger,
        PluginRegistryBuilder registryBuilder)
    {
        var usableSources = new List<ProtobufSchemaSource>();

        foreach (var source in ProtobufSchemaFolderLoader.Discover(config.PluginFolders, protobufLogger))
        {
            try
            {
                var probe = new ProtobufSchemaRegistry(source.Manifest, source.SchemaRoot, protobufLogger);

                if (!probe.HasAnySchemas)
                {
                    registryBuilder.AddDiagnostic(new PluginDiagnosticEntry
                    {
                        Source = "protobuf",
                        SourcePath = source.SchemaRoot,
                        Severity = DiagnosticSeverity.Warning,
                        Message = $"No schemas compiled from {source.SchemaRoot}.",
                        Details = string.Join(" ", probe.Diagnostics)
                    });

                    continue;
                }

                usableSources.Add(source);
            }
            catch (Exception ex)
            {
                registryBuilder.AddDiagnostic(new PluginDiagnosticEntry
                {
                    Source = "protobuf",
                    SourcePath = source.SchemaRoot,
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Failed to load protobuf schemas from {source.SchemaRoot}.",
                    Details = ex.Message
                });
            }
        }

        return usableSources;
    }

    private static void CombineAndRegisterProtobufSources(
        List<ProtobufSchemaSource> usableSources,
        ILogger protobufLogger,
        Func<IReadOnlyList<ProtobufSchemaSource>, ILogger, ProtobufSchemaRegistry> combineProtobufSources,
        PluginRegistryBuilder registryBuilder)
    {
        try
        {
            var schemaRegistry = combineProtobufSources(usableSources, protobufLogger);

            if (schemaRegistry.HasAnySchemas)
            {
                registryBuilder.RegisterDetector(new ProtobufPayloadDetector(schemaRegistry));
                registryBuilder.RegisterDecoder(new ProtobufPayloadDecoder(schemaRegistry));

                foreach (var source in usableSources)
                    registryBuilder.RegisterPackagePath(source.SchemaRoot);
            }
            else
            {
                foreach (var source in usableSources)
                {
                    registryBuilder.AddDiagnostic(new PluginDiagnosticEntry
                    {
                        Source = "protobuf",
                        SourcePath = source.SchemaRoot,
                        Severity = DiagnosticSeverity.Error,
                        Message = "Combined protobuf schema registry produced no usable schemas.",
                        Details = string.Join(" ", schemaRegistry.Diagnostics)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var source in usableSources)
            {
                registryBuilder.AddDiagnostic(new PluginDiagnosticEntry
                {
                    Source = "protobuf",
                    SourcePath = source.SchemaRoot,
                    Severity = DiagnosticSeverity.Error,
                    Message = "Failed to build combined protobuf schema registry from usable sources.",
                    Details = ex.Message
                });
            }
        }
    }

    private static void LoadAssemblyPlugins(
        PluginConfig config,
        ILoggerFactory loggerFactory,
        PluginAssemblyCache? assemblyCache,
        PluginRegistryBuilder registryBuilder)
    {
        try
        {
            var loadResult = (assemblyCache ?? new PluginAssemblyCache())
                .GetOrLoad(config, loggerFactory);

            foreach (var diagnostic in loadResult.Diagnostics)
                registryBuilder.AddDiagnostic(diagnostic);

            foreach (var path in loadResult.LoadedPaths)
                registryBuilder.RegisterPackagePath(path);

            foreach (var id in loadResult.DisabledIds)
                registryBuilder.NoteInstalledDisabledPlugin(id);

            foreach (var plugin in loadResult.Plugins)
                registryBuilder.RegisterPlugin(plugin.PluginId, ctx => plugin.RegisterServices(ctx));
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(MqttProbePluginStartup).FullName!)
                .LogError(ex, "Plugin loading failed; falling back to built-ins only.");
        }
    }
}
