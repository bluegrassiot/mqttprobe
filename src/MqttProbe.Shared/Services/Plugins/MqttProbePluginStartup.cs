using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.BuiltIn;
using MqttProbe.Services.Plugins.Loading;
using MqttProbe.Services.Plugins.Protobuf;
using MqttProbe.Services.Plugins.Registry;

namespace MqttProbe.Services.Plugins;

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

        var registryBuilder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(registryBuilder);

        var protobufLogger = loggerFactory.CreateLogger<ProtobufSchemaRegistry>();
        var usableSources = new List<ProtobufSchemaSource>();

        foreach (var source in ProtobufSchemaFolderLoader.Discover(config.PluginFolders, protobufLogger))
        {
            // Each source is probed on its own so one malformed bundle cannot
            // disable protobuf decoding for every other bundle.
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

        if (usableSources.Count > 0)
        {
            // Combining sources is a distinct operation from probing them individually, so it can
            // still fail even though every source compiled on its own; falling back to built-ins
            // here matches the resilience the per-source loop already provides.
            try
            {
                var schemaRegistry = combineProtobufSources(usableSources, protobufLogger);

                if (schemaRegistry.HasAnySchemas)
                {
                    registryBuilder.RegisterDetector(new ProtobufPayloadDetector(schemaRegistry));
                    registryBuilder.RegisterDecoder(new ProtobufPayloadDecoder(schemaRegistry));

                    // Only mark sources as loaded once the combined registry that actually
                    // backs the decoder/detector has been built successfully; registering
                    // them earlier (per-source) would report a package as Active even when
                    // the combine step below never wired up any decoding for it.
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

        try
        {
            var loadResult = (assemblyCache ?? new PluginAssemblyCache())
                .GetOrLoad(config, loggerFactory);

            foreach (var diagnostic in loadResult.Diagnostics)
                registryBuilder.AddDiagnostic(diagnostic);

            foreach (var path in loadResult.LoadedPaths)
                registryBuilder.RegisterPackagePath(path);

            foreach (var plugin in loadResult.Plugins)
                registryBuilder.RegisterPlugin(plugin.PluginId, ctx => plugin.RegisterServices(ctx));
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(MqttProbePluginStartup).FullName!)
                .LogError(ex, "Plugin loading failed; falling back to built-ins only.");
        }

        return registryBuilder.Build(config.DisabledPluginIds, config.Overrides);
    }
}
