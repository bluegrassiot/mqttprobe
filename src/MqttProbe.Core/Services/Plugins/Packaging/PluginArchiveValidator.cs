using System.IO.Compression;
using MqttProbe.Core.Models.Plugins;

namespace MqttProbe.Core.Services.Plugins.Packaging;

public static class PluginArchiveValidator
{
    private static readonly string[] _sharedExtensions = [".proto", ".json", ".md", ".txt"];

    private static readonly string[] _assemblyOnlyExtensions = [".dll", ".pdb"];

    public static bool IsSafeEntryPath(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.Contains('\0'))
        {
            return false;
        }

        var normalized = entryName.Replace('\\', '/');

        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || HasDriveLetterPrefix(normalized))
        {
            return false;
        }

        return !normalized.Split('/').Contains("..");
    }

    // Path.IsPathRooted only recognises a drive-letter prefix (e.g. "C:") as rooted on
    // Windows, so this must be checked explicitly to keep validation identical on Linux CI.
    private static bool HasDriveLetterPrefix(string normalized)
    {
        return normalized.Length >= 2 && normalized[1] == ':' && char.IsAsciiLetter(normalized[0]);
    }

    public static bool TryResolveDestination(string rootDirectory, string entryName, out string destination)
    {
        destination = string.Empty;

        if (!IsSafeEntryPath(entryName))
        {
            return false;
        }

        var root = Path.GetFullPath(rootDirectory);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Combine(root, entryName.Replace('\\', '/')));

        if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        destination = candidate;
        return true;
    }

    public static PluginValidationResult ValidateEntries(
        ZipArchive archive,
        string kind,
        PluginArchiveLimits limits)
    {
        if (archive.Entries.Count > limits.MaxEntries)
        {
            return PluginValidationResult.Fail(
                $"Package contains {archive.Entries.Count} entries, exceeding the limit of {limits.MaxEntries}.");
        }

        var allowed = kind == PluginPackageKinds.Assembly
            ? _sharedExtensions.Concat(_assemblyOnlyExtensions).ToArray()
            : _sharedExtensions;

        long totalUncompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in archive.Entries)
        {
            if (!IsSafeEntryPath(entry.FullName))
            {
                return PluginValidationResult.Fail(
                    $"Package contains an unsafe entry path: {entry.FullName}"); // DevSkim: ignore DS172412 - the word in a message, not the keyword
            }

            if ((entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                && entry.Length == 0 && entry.CompressedLength == 0)
            {
                continue;
            }

            var extension = Path.GetExtension(entry.FullName);

            if (!allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return PluginValidationResult.Fail(
                    $"Package contains a file type that is not permitted for a '{kind}' package: {extension} ({entry.FullName})");
            }

            totalUncompressed += entry.Length;
            totalCompressed += entry.CompressedLength;

            if (totalUncompressed > limits.MaxUncompressedBytes)
            {
                return PluginValidationResult.Fail(
                    $"Package expands to more than the permitted {limits.MaxUncompressedBytes} uncompressed bytes.");
            }
        }

        if (totalCompressed > 0 && totalUncompressed / (double)totalCompressed > limits.MaxCompressionRatio)
        {
            return PluginValidationResult.Fail(
                $"Package compression ratio exceeds the permitted {limits.MaxCompressionRatio}:1.");
        }

        return PluginValidationResult.Success;
    }
}
