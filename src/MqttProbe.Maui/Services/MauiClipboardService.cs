using MqttProbe.Core.Services.Platform;

namespace MqttProbe.Maui.Services;

public class MauiClipboardService : IClipboardService
{
    public Task<string?> GetTextAsync() =>
        Clipboard.Default.GetTextAsync();

    public Task WriteTextAsync(string text) =>
        Clipboard.Default.SetTextAsync(text);
}
