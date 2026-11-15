using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Charts;

internal sealed class ChartExportInterop(IJSRuntime js) : IAsyncDisposable
{
    internal const string ModulePath =
        "./_content/MqttProbe.UI/js/chart-export.js?v=1";

    // Disposal waits on this gate so an in-flight capture cannot outlive the card.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _snapshots = [];
    private IJSObjectReference? _module;
    private volatile bool _disposed;

    internal async Task<ChartExportSnapshot?> CaptureAsync(string chartId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return null;

            var module = await EnsureModuleAsync();
            if (module is null)
                return null;

            var snapshot = await module.InvokeAsync<ChartExportSnapshot?>(
                "captureSnapshot", chartId);
            if (snapshot is not null)
                _snapshots.Add(snapshot.Id);

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ExportAsync(
        string snapshotId,
        string[] selectedSeriesIds,
        string format,
        string fileName)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var module = await EnsureModuleAsync()
                ?? throw new InvalidOperationException("the chart export module is unavailable");

            await module.InvokeVoidAsync(
                "exportSnapshot", snapshotId, selectedSeriesIds, format, fileName);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ReleaseAsync(string snapshotId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _module is null || !_snapshots.Remove(snapshotId))
                return;

            await InvokeReleaseAsync(_module, snapshotId);
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
            if (module is null)
                return;

            try
            {
                foreach (var snapshotId in _snapshots)
                    await InvokeReleaseAsync(module, snapshotId);
            }
            finally
            {
                _snapshots.Clear();
                await DisposeModuleAsync(module);
            }
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

    private static async Task InvokeReleaseAsync(IJSObjectReference module, string snapshotId)
    {
        try
        {
            await module.InvokeVoidAsync("releaseSnapshot", snapshotId);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the module with the page.
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
