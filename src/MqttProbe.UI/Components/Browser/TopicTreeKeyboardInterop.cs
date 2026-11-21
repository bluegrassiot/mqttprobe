using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Browser;

internal sealed class TopicTreeKeyboardInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/topic-tree-keyboard.js";

    private IJSObjectReference? _module;
    private bool _disposed;

    internal async Task<bool> AttachAsync(
        ElementReference tree,
        string token,
        DotNetObjectReference<TopicBrowser> self)
    {
        var module = await ModuleAsync();
        return module is not null && await module.InvokeAsync<bool>("attach", tree, token, self);
    }

    internal async Task DetachAsync(string token)
    {
        var module = await ModuleAsync();
        if (module is null) return;

        await module.InvokeVoidAsync("detach", token);
    }

    internal async Task MoveFocusAsync(ElementReference tree, string key, int index, float itemHeight)
    {
        var module = await ModuleAsync();
        if (module is null) return;

        await module.InvokeVoidAsync("moveFocus", tree, key, index, itemHeight);
    }

    internal async Task RestoreFocusAsync(ElementReference tree, string key, int index, float itemHeight)
    {
        var module = await ModuleAsync();
        if (module is null) return;

        await module.InvokeVoidAsync("restoreFocus", tree, key, index, itemHeight);
    }

    internal async Task RetainFocusAsync(ElementReference tree, string key, int index, float itemHeight)
    {
        var module = await ModuleAsync();
        if (module is null) return;

        await module.InvokeVoidAsync("retainFocus", tree, key, index, itemHeight);
    }

    private async Task<IJSObjectReference?> ModuleAsync()
    {
        if (_disposed) return null;

        _module ??= await js.InvokeAsync<IJSObjectReference?>("import", ModulePath);
        return _module;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        var module = _module;
        _module = null;
        if (module is null) return;

        try
        {
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the module with the page.
        }
    }
}
