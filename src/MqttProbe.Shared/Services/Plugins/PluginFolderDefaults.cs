using MqttProbe.Models.Plugins;

namespace MqttProbe.Services.Plugins;

public static class PluginFolderDefaults
{
    public static void Apply(PluginConfig config, string pathBase, params string[] defaultFoldersWhenEmpty)
    {
        for (var i = 0; i < config.PluginFolders.Count; i++)
        {
            var folder = config.PluginFolders[i];
            if (!Path.IsPathRooted(folder))
                config.PluginFolders[i] = Path.GetFullPath(folder, pathBase);
        }

        if (config.PluginFolders.Count > 0)
            return;

        foreach (var folder in defaultFoldersWhenEmpty)
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            var resolved = Path.IsPathRooted(folder)
                ? Path.GetFullPath(folder)
                : Path.GetFullPath(folder, pathBase);

            if (!config.PluginFolders.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                config.PluginFolders.Add(resolved);
        }
    }
}
