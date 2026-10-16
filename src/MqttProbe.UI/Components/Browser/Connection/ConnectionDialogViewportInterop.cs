using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Browser.Connection;

// Owns the dialog's viewport module; releases are attachment-scoped, so a module that never attached cannot detach a newer dialog's.
internal sealed class ConnectionDialogViewportInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/connection-dialog-viewport.js";

    private IJSObjectReference? _module;
    private int? _attachment;
    private bool _disposed;

    internal async Task AttachAsync(ElementReference content)
    {
        var module = await js.InvokeAsync<IJSObjectReference?>("import", ModulePath);
        if (module is null)
            return;

        if (_disposed)
        {
            await ReleaseModuleAsync(module, attachment: null);
            return;
        }

        // The module stays locally owned until attach returns, so a concurrent Dispose cannot leak the browser-side attachment.
        int? attachment;
        try
        {
            attachment = await module.InvokeAsync<int?>("attach", content);
        }
        catch
        {
            await ReleaseModuleAsync(module, attachment: null);
            throw;
        }

        if (_disposed)
        {
            await ReleaseModuleAsync(module, attachment);
            return;
        }

        _module = module;
        _attachment = attachment;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        var module = _module;
        var attachment = _attachment;
        _module = null;
        _attachment = null;
        if (module is not null)
            await ReleaseModuleAsync(module, attachment);
    }

    private static async Task ReleaseModuleAsync(IJSObjectReference module, int? attachment)
    {
        try
        {
            if (attachment is { } token)
                await module.InvokeVoidAsync("detach", token);

            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
    }
}
