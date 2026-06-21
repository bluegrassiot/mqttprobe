using MqttProbe.Services.Platform;

namespace MqttProbe.Services;

public class MauiClipboardService : IClipboardService
{
    public Task<string?> GetTextAsync() =>
        Clipboard.Default.GetTextAsync();

    public Task WriteTextAsync(string text) =>
        Clipboard.Default.SetTextAsync(text);
}
