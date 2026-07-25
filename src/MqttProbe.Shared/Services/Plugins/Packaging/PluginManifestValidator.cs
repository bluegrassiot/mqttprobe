using System.Text.Json;
using System.Text.RegularExpressions;
using MqttProbe.Models.Plugins;

namespace MqttProbe.Services.Plugins.Packaging;

public sealed record PluginValidationResult(bool IsValid, string? Error)
{
    public static readonly PluginValidationResult Success = new(true, null);

    public static PluginValidationResult Fail(string error) => new(false, error);
}

public static partial class PluginManifestValidator
{
    public const string FileName = "mqttprobe-plugin.json";

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+].*)?$")]
    private static partial Regex SemVerPattern();

    public static PluginPackageManifest? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginPackageManifest>(json, _options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static PluginValidationResult Validate(PluginPackageManifest? manifest, string appVersion)
    {
        if (manifest is null)
        {
            return PluginValidationResult.Fail($"Package does not contain a readable {FileName}.");
        }

        if (!IdPattern().IsMatch(manifest.Id))
        {
            return PluginValidationResult.Fail(
                $"Package id '{manifest.Id}' is invalid. Use lowercase letters, digits, dot, dash or underscore, starting with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return PluginValidationResult.Fail("Package name is required.");
        }

        if (!SemVerPattern().IsMatch(manifest.Version))
        {
            return PluginValidationResult.Fail(
                $"Package version '{manifest.Version}' is not a three-part SemVer value such as 1.0.0.");
        }

        if (!PluginPackageKinds.IsKnown(manifest.Kind))
        {
            return PluginValidationResult.Fail(
                $"Package kind '{manifest.Kind}' is not supported. Expected '{PluginPackageKinds.ProtobufSchemas}' or '{PluginPackageKinds.Assembly}'.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinAppVersion))
        {
            if (!PluginVersion.TryParse(manifest.MinAppVersion, out var required))
            {
                return PluginValidationResult.Fail(
                    $"Package minAppVersion '{manifest.MinAppVersion}' is not a valid version.");
            }

            if (PluginVersion.TryParse(appVersion, out var current) && current < required)
            {
                return PluginValidationResult.Fail(
                    $"Package requires MQTT Probe {manifest.MinAppVersion} or newer; this build is {appVersion}.");
            }
        }

        return PluginValidationResult.Success;
    }
}
