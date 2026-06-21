namespace MqttProbe.Services;

public static class FileHelper
{
    public static async Task WriteAtomicallyAsync(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }
}
