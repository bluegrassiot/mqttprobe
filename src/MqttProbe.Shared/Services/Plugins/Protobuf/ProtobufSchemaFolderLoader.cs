using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Plugins;

namespace MqttProbe.Services.Plugins.Protobuf;

public static class ProtobufSchemaFolderLoader
{
    public const string FolderName = "protobuf";
    public const string ManifestFileName = "protobuf-schemas.json";

    private static readonly JsonSerializerOptions _manifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<ProtobufSchemaSource> Discover(
        IEnumerable<string> pluginFolders,
        ILogger? logger = null)
    {
        var sources = new List<ProtobufSchemaSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pluginFolder in pluginFolders)
        {
            var protobufFolder = Path.Combine(pluginFolder, FolderName);
            if (!Directory.Exists(protobufFolder))
                continue;

            TryAddSource(protobufFolder, required: false);
            foreach (var subfolder in Directory.GetDirectories(protobufFolder))
                TryAddSource(subfolder, required: false);
        }

        if (sources.Count == 0)
            logger?.LogDebug("No protobuf schema manifests found in any plugin folder.");

        return sources;

        void TryAddSource(string schemaRoot, bool required)
        {
            if (!seen.Add(schemaRoot))
                return;

            var manifestPath = Path.Combine(schemaRoot, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                if (required)
                {
                    logger?.LogWarning(
                        "Protobuf schema folder {Folder} has no {Manifest}; no schemas loaded from it.",
                        schemaRoot, ManifestFileName);
                }
                return;
            }

            ProtobufSchemaManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ProtobufSchemaManifest>(
                    File.ReadAllText(manifestPath), _manifestOptions);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to read protobuf manifest {Path}; skipping.", manifestPath);
                return;
            }

            if (manifest is null || manifest.Schemas.Count == 0)
            {
                logger?.LogWarning("Protobuf manifest {Path} declares no schemas.", manifestPath);
                return;
            }

            sources.Add(new ProtobufSchemaSource(manifest, schemaRoot));
            logger?.LogInformation(
                "Loaded {Count} protobuf schema mapping(s) from {Path}.", manifest.Schemas.Count, manifestPath);
        }
    }
}
