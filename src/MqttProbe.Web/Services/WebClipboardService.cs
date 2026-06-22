using Microsoft.JSInterop;
using MqttProbe.Services.Platform;

namespace MqttProbe.Web.Services;

public class WebClipboardService : IClipboardService
{
    private readonly IJSRuntime _js;

    public WebClipboardService(IJSRuntime js) => _js = js;

    public async Task<string?> GetTextAsync() =>
        await _js.InvokeAsync<string>("navigator.clipboard.readText");

    public async Task WriteTextAsync(string text) =>
        await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
}
