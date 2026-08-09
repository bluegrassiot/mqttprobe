namespace MqttProbe.Core.Services.Security;

public interface ICertificateFilePicker
{
    public Task<byte[]?> PickFileAsync(string title, string[] extensions, long maxBytes);
}
