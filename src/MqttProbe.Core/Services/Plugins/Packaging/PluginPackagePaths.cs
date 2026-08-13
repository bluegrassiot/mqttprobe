using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Services.Plugins.Packaging;

public static class PluginPackagePaths
{
    public const string StagingFolderName = ".staging";

    public const string PendingFolderName = ".pending";

    public static string ResolveInstallPath(string pluginFolder, string kind, string id) =>
        kind == PluginPackageKinds.ProtobufSchemas
            ? Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName, id)
            : Path.Combine(pluginFolder, id);

    public static bool IsReservedDirectoryName(string directoryName) =>
        directoryName.StartsWith('.');

    public static string BackupDirectoryName(string installPath) =>
        "." + Path.GetFileName(installPath) + ".previous-" + Guid.NewGuid().ToString("N");
}
