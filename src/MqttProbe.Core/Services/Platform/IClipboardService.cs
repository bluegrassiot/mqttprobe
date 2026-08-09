namespace MqttProbe.Core.Services.Platform;

public interface IClipboardService
{
    public Task<string?> GetTextAsync();
    public Task WriteTextAsync(string text);
}
