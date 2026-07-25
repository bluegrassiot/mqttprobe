using MqttProbe.Models.Plugins;

namespace MqttProbe.Services.Plugins.Packaging;

public enum PluginStatus
{
    Active,
    Failed,
    PendingActivation,
    PendingRestart,
    Disabled
}

public sealed record InstalledPlugin(
    PluginPackageManifest Manifest,
    string InstallPath,
    PluginStatus Status,
    string? StatusDetail);
