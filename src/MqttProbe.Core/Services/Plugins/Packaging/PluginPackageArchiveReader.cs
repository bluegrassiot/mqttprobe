using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Services.Plugins.Packaging;

internal sealed class PluginPackageArchiveReader(
    PluginConfig config,
    IAppInfoService appInfo,
    PluginArchiveLimits limits,
    ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions _schemaManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<PluginPackageArchiveReadResult> ReadAsync(
        Stream package,
        string archivePath,
        string extractPath,
        CancellationToken ct)
    {
        var copied = await CopyWithLimitAsync(package, archivePath, ct);

        if (copied is null)
        {
            return Failure(
                archivePath,
                extractPath,
                $"Package exceeds the maximum upload size of {limits.MaxCompressedBytes} bytes.");
        }

        await using var archive = await ZipFile.OpenReadAsync(archivePath, ct);
        var manifestEntry = archive.GetEntry(PluginManifestValidator.FileName);

        if (manifestEntry is null)
        {
            return Failure(
                archivePath,
                extractPath,
                $"Package does not contain {PluginManifestValidator.FileName} at its root.");
        }

        using var reader = new StreamReader(await manifestEntry.OpenAsync(ct));
        var manifest = PluginManifestValidator.Deserialize(await reader.ReadToEndAsync(ct));

        var manifestResult = PluginManifestValidator.Validate(manifest, appInfo.GetVersion());

        if (!manifestResult.IsValid)
        {
            return Failure(archivePath, extractPath, manifestResult.Error!);
        }

        var kindResult = ValidateKindIsPermitted(manifest!.Kind);

        if (!kindResult.IsValid)
        {
            return Failure(archivePath, extractPath, kindResult.Error!);
        }

        var entryResult = PluginArchiveValidator.ValidateEntries(archive, manifest.Kind, limits);

        if (!entryResult.IsValid)
        {
            return Failure(archivePath, extractPath, entryResult.Error!);
        }

        Extract(archive, extractPath, ct);

        var contentResult = ValidateExtractedContent(manifest, extractPath, ct);

        if (!contentResult.IsValid)
        {
            return Failure(archivePath, extractPath, contentResult.Error!);
        }

        return new PluginPackageArchiveReadResult(manifest, archivePath, extractPath, null);
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

            if (total > limits.MaxCompressedBytes)
            {
                return null;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return total;
    }

    private PluginValidationResult ValidateKindIsPermitted(string kind)
    {
        if (kind != PluginPackageKinds.Assembly)
        {
            return PluginValidationResult.Success;
        }

        if (!config.AllowBinaryPackages)
        {
            return PluginValidationResult.Fail(
                "Binary plugin packages are disabled on this instance. An administrator re-enables them by setting Plugins:AllowBinaryPackages back to true.");
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

        return ValidateProtobufSchemaPackage(extractPath, ct);
    }

    private PluginValidationResult ValidateProtobufSchemaPackage(string extractPath, CancellationToken ct)
    {
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
            schemaManifest, extractPath, loggerFactory.CreateLogger<ProtobufSchemaRegistry>());

        if (!registry.HasAnySchemas)
        {
            return PluginValidationResult.Fail(
                $"No schemas compiled from this package. {string.Join(" ", registry.Diagnostics)}".Trim());
        }

#pragma warning disable S3267 // ThrowIfCancellationRequested must run per iteration
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

                if (totalUncompressed > limits.MaxUncompressedBytes)
                {
                    throw new PluginPackageExpansionLimitExceededException(
                        $"Package expands to more than the permitted {limits.MaxUncompressedBytes} uncompressed bytes.");
                }

                target.Write(buffer, 0, read);
            }
        }
    }

    private static PluginPackageArchiveReadResult Failure(
        string archivePath, string extractPath, string error) =>
        new(null, archivePath, extractPath, PluginInstallOutcome.Fail(error));
}

internal sealed record PluginPackageArchiveReadResult(
    PluginPackageManifest? Manifest,
    string ArchivePath,
    string ExtractPath,
    PluginInstallOutcome? Failure);

internal sealed class PluginPackageExpansionLimitExceededException : Exception
{
    public PluginPackageExpansionLimitExceededException(string message) : base(message)
    {
    }
}
