namespace MqttProbe.Desktop.Services.Security;

public interface IMacKeychainNative
{
    public int CopyMatching(string service, string account, out byte[]? data);
    public int Add(string service, string account, ReadOnlySpan<byte> data);
    public int Update(string service, string account, ReadOnlySpan<byte> data);
    public int Delete(string service, string account);
}
