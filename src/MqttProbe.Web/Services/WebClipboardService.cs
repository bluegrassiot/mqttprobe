using CurrieTechnologies.Razor.Clipboard;
using MqttProbe.Services.Platform;

namespace MqttProbe.Web.Services;

public class WebClipboardService : IClipboardService
{
    private readonly ClipboardService _clipboard;

    public WebClipboardService(ClipboardService clipboard) => _clipboard = clipboard;

    public async Task<string?> GetTextAsync() => await _clipboard.ReadTextAsync();

    public async Task WriteTextAsync(string text) => await _clipboard.WriteTextAsync(text);
}
