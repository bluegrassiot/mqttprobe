namespace MqttProbe.Services.Configuration;

/// <summary>
/// One-time copy of user config from the legacy publisher directory
/// (%LOCALAPPDATA%\User Name\..., shipped in v1.0.1 and earlier) to the
/// current publisher directory. Copy, not move: the legacy directory is
/// intentionally left behind as a backup.
/// </summary>
public static class ConfigMigrator
{
    public static bool MigrateIfNeeded(string legacyDir, string newDir)
    {
        if (Directory.Exists(newDir) || !Directory.Exists(legacyDir))
            return false;

        var stagingDir = $"{newDir}.migrating-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(stagingDir);
            foreach (var file in Directory.GetFiles(legacyDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacyDir, file);
                var destination = Path.Combine(stagingDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }

            Directory.Move(stagingDir, newDir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
            return false;
        }
    }
}
