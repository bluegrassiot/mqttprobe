namespace MqttProbe.Services.Platform;

public interface IClipboardService
{
    public Task<string?> GetTextAsync();
    public Task WriteTextAsync(string text);
}
