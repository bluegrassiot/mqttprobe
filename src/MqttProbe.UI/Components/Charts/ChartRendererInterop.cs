using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Charts;

internal sealed class ChartRendererInterop(IJSRuntime js) : IAsyncDisposable
{
    // Bump when the exported module contract changes so browsers cannot reuse an older module URL.
    internal const string ModulePath =
        "./_content/MqttProbe.UI/js/chart-renderer.js?v=3";

    // Disposal waits on this gate so an in-flight create cannot outlive the card.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IJSObjectReference? _module;
    private volatile bool _disposed;

    internal async Task<bool> CreateAsync(
        ElementReference container,
        string chartId,
        DotNetObjectReference<ChartCard> owner,
        ChartRendererSpec spec)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return false;

            var module = await EnsureModuleAsync();
            if (module is null)
                return false;

            return await module.InvokeAsync<bool>("createChart", container, chartId, owner, spec);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> UpdateAsync(string chartId, ChartRendererSpec spec)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _module is null)
                return false;

            return await _module.InvokeAsync<bool>("updateChart", chartId, spec);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task DisposeChartAsync(string chartId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_module is not null)
                await _module.InvokeAsync<bool>("disposeChart", chartId);
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
