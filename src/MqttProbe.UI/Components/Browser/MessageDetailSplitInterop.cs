using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Browser;

internal sealed class MessageDetailSplitInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/message-detail-split.js";

    // Disposal waits on this gate so an in-flight import cannot assign the module after disposal.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IJSObjectReference? _module;
    private volatile bool _disposed;

    // False while the split has no measurable height, so the caller can retry on the next render.
    internal async Task<bool> FitToContentAsync(ElementReference metaPane)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return false;

            var module = await EnsureModuleAsync();
            if (module is null)
                return false;

            return await module.InvokeAsync<bool>("fitToContent", metaPane);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            var module = _module;
            _module = null;
            if (module is not null)
                await DisposeModuleAsync(module);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IJSObjectReference?> EnsureModuleAsync()
    {
        if (_module is not null)
            return _module;

        var module = await js.InvokeAsync<IJSObjectReference?>("import", ModulePath);
        if (module is null)
            return null;

        if (_disposed)
        {
            await DisposeModuleAsync(module);
            return null;
        }

        _module = module;
        return module;
    }

    private static async Task DisposeModuleAsync(IJSObjectReference module)
    {
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
