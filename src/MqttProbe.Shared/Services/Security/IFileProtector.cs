namespace MqttProbe.Services.Security;

public interface IFileProtector
{
    public bool ApplyProtections(string path);
    public bool TryDelete(string path);
    public bool TryMoveToQuarantine(string sourcePath, string quarantinePath);
}
