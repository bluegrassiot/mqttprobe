namespace MqttProbe.Services.Security;

public class DefaultFileProtector : IFileProtector
{
    public bool ApplyProtections(string path) => true;

    public bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    public bool TryMoveToQuarantine(string sourcePath, string quarantinePath)
    {
        try { File.Move(sourcePath, quarantinePath); return true; }
        catch { return false; }
    }
}
