namespace MqttProbe.Desktop.Services.Security;

public interface IRawSecretKeyFile
{
    public bool Exists { get; }
    public byte[]? TryRead();
    public void Write(ReadOnlySpan<byte> key);
    public bool TryDelete();
}
