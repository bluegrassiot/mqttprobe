using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Browser;

// Owns the message list's keyboard module; releases are attachment-scoped, so a stale detach cannot remove a newer list's listener.
internal sealed class MessageListKeyboardInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/message-list-keyboard.js";

    private IJSObjectReference? _module;
    private int? _attachment;
    private bool _disposed;

    internal async Task AttachAsync(ElementReference list)
    {
        if (_disposed || _attachment is not null)
            return;

        var module = await ModuleAsync();
        if (module is null || _disposed)
            return;

        _attachment = await module.InvokeAsync<int?>("attach", list);
    }

    internal async Task DetachAsync()
    {
        if (_module is null || _attachment is not { } attachment)
            return;

        _attachment = null;
        await _module.InvokeVoidAsync("detach", attachment);
    }

    internal async Task RevealAsync(ElementReference list, int index)
    {
        if (_disposed || index < 0)
            return;

        var module = await ModuleAsync();
        if (module is null || _disposed)
            return;

        await module.InvokeVoidAsync("reveal", list, index);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        var module = _module;
        var attachment = _attachment;
        _module = null;
        _attachment = null;
        if (module is null)
            return;

        try
        {
            // The module outlives the component, so the module-level attachment has to be released by hand.
            if (attachment is { } token)
                await module.InvokeVoidAsync("detach", token);

            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
    }

    private async Task<IJSObjectReference?> ModuleAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference?>("import", ModulePath);
        return _module;
    }
}
