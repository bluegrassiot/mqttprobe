using MqttProbe.Core.Models.Plugins;

namespace MqttProbe.Core.Services.Plugins.Packaging;

public sealed record PluginInstallOutcome(
    bool Succeeded,
    string? Error,
    PluginPackageManifest? Manifest,
    string? InstallPath,
    bool RequiresRestart)
{
    public static PluginInstallOutcome Fail(string error) =>
        new(false, error, null, null, false);

    public static PluginInstallOutcome Success(
        PluginPackageManifest manifest, string installPath, bool requiresRestart) =>
        new(true, null, manifest, installPath, requiresRestart);
}
