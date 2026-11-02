using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Sparkplug;

internal sealed class SparkplugSplitBoundsInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/sparkplug-nodes-split-bounds.js";

    // Disposal waits on this gate so an in-flight attach can still detach from a live module.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IJSObjectReference? _module;
    private int? _attachment;
    private volatile bool _disposed;

    internal async Task<bool> InstallAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return false;

            return await EnsureModuleAsync() is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task AttachAsync(ElementReference wrapper)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            var module = await EnsureModuleAsync();
            if (module is null)
                return;

            await ReleaseAttachmentAsync();

            var attachment = await module.InvokeAsync<int?>("attach", wrapper);
            if (_disposed)
            {
                if (attachment is { } token)
                    await TryDetachAsync(module, token);
                return;
            }

            _attachment = attachment;
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
            var attachment = _attachment;
            _module = null;
            _attachment = null;

            if (module is not null)
                await ReleaseModuleAsync(module, attachment);
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

        try
        {
            await module.InvokeVoidAsync("install");
        }
        catch
        {
            await DisposeModuleAsync(module);
            throw;
        }

        if (_disposed)
        {
            await DisposeModuleAsync(module);
            return null;
        }

        _module = module;
        return module;
    }

    private async Task ReleaseAttachmentAsync()
    {
        var module = _module;
        var attachment = _attachment;
        _attachment = null;
        if (module is not null && attachment is { } token)
            await TryDetachAsync(module, token);
    }

    private static async Task TryDetachAsync(IJSObjectReference module, int token)
    {
        try
        {
            await module.InvokeVoidAsync("detach", token);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
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
