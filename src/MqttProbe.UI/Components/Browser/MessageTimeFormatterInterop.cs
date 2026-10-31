using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;

namespace MqttProbe.UI.Components.Browser;

internal sealed record MessageTimeFormatRequest(
    [property: JsonPropertyName("key")] Guid Key,
    [property: JsonPropertyName("epochMs")] long EpochMs,
    [property: JsonPropertyName("detail")] bool Detail);

internal interface IMessageTimeFormatter : IAsyncDisposable
{
    public Task<MessageTimeFormatResult?> FormatAsync(
        IReadOnlyList<MessageTimeFormatRequest> items,
        MessageTimePreferences preferences);
}

internal sealed class MessageTimeFormatterInterop(IJSRuntime js) : IMessageTimeFormatter
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/message-time-format.js";

    private IJSObjectReference? _module;
    private bool _disposed;

    public async Task<MessageTimeFormatResult?> FormatAsync(
        IReadOnlyList<MessageTimeFormatRequest> items,
        MessageTimePreferences preferences)
    {
        if (_disposed || items.Count == 0)
            return null;

        var module = await ModuleAsync();
        if (module is null || _disposed)
            return null;

        return await module.InvokeAsync<MessageTimeFormatResult?>(
            "formatMessages",
            items,
            new { zone = preferences.Zone, clockFormat = preferences.ClockFormat });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        var module = _module;
        _module = null;
        if (module is null)
            return;

        try
        {
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser releases the module with the page.
        }
    }

    private async Task<IJSObjectReference?> ModuleAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference?>("import", ModulePath);
        return _module;
    }
}
