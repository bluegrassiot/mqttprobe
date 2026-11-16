using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;

namespace MqttProbe.UI.Services.Formatting;

internal sealed record DeviceTimeFormatRequest(
    [property: JsonPropertyName("key")] Guid Key,
    [property: JsonPropertyName("epochMs")] long EpochMs,
    [property: JsonPropertyName("detail")] bool Detail);

internal sealed record DeviceTimeFormatItem(
    Guid Key,
    string TimeText,
    string DateText,
    string FullText,
    string ZoneAbbreviation,
    string ZoneName,
    string OffsetLabel);

internal sealed record DeviceTimeFormatResult(
    string ZoneLabel,
    string ZoneName,
    List<DeviceTimeFormatItem>? Items);

internal interface IDeviceTimeFormatter : IAsyncDisposable
{
    public Task<DeviceTimeFormatResult?> FormatAsync(
        IReadOnlyList<DeviceTimeFormatRequest> items,
        MessageTimePreferences preferences);

    public Task<bool> EnsureBridgeAsync();
}

internal sealed class DeviceTimeFormatterInterop(IJSRuntime js) : IDeviceTimeFormatter
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/message-time-format.js";

    private readonly Lock _gate = new();
    private Task<IJSObjectReference?>? _moduleLoad;
    private bool _disposed;

    // A client-to-server interop frame (this reply) is capped at 32 KB, and it carries one
    // formatted item per requested row, so a long message list must travel in bounded batches.
    private const int MaxItemsPerRequest = 64;

    public async Task<DeviceTimeFormatResult?> FormatAsync(
        IReadOnlyList<DeviceTimeFormatRequest> items,
        MessageTimePreferences preferences)
    {
        if (_disposed || items.Count == 0)
            return null;

        var module = await ModuleAsync();
        if (module is null || _disposed)
            return null;

        if (items.Count <= MaxItemsPerRequest)
            return await FormatBatchAsync(module, items, preferences);

        var merged = new List<DeviceTimeFormatItem>(items.Count);
        string? zoneLabel = null;
        string? zoneName = null;
        for (var start = 0; start < items.Count; start += MaxItemsPerRequest)
        {
            if (_disposed)
                return null;

            var count = Math.Min(MaxItemsPerRequest, items.Count - start);
            var batch = new List<DeviceTimeFormatRequest>(count);
            for (var index = 0; index < count; index++)
                batch.Add(items[start + index]);

            var result = await FormatBatchAsync(module, batch, preferences);
            if (result?.Items is not { } batchItems)
                return null;

            if (zoneLabel is null)
            {
                zoneLabel = result.ZoneLabel;
                zoneName = result.ZoneName;
            }

            merged.AddRange(batchItems);
        }

        return new DeviceTimeFormatResult(zoneLabel ?? "", zoneName ?? "", merged);
    }

    private static ValueTask<DeviceTimeFormatResult?> FormatBatchAsync(
        IJSObjectReference module,
        IReadOnlyList<DeviceTimeFormatRequest> items,
        MessageTimePreferences preferences) =>
        module.InvokeAsync<DeviceTimeFormatResult?>(
            "formatInstants",
            items,
            new { zone = preferences.Zone, clockFormat = preferences.ClockFormat });

    public async Task<bool> EnsureBridgeAsync()
    {
        if (_disposed)
            return false;

        var module = await ModuleAsync();
        if (module is null || _disposed)
            return false;

        await module.InvokeVoidAsync("installTimeBridge");
        return !_disposed;
    }

    public async ValueTask DisposeAsync()
    {
        Task<IJSObjectReference?>? load;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            load = _moduleLoad;
            _moduleLoad = null;
        }

        if (load is null)
            return;

        IJSObjectReference? module;
        try
        {
            module = await load;
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException)
        {
            return;
        }

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

    private Task<IJSObjectReference?> ModuleAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.FromResult<IJSObjectReference?>(null);

            var load = _moduleLoad;
            if (load is not null && !load.IsFaulted && !load.IsCanceled)
                return load;

            load = js.InvokeAsync<IJSObjectReference?>("import", ModulePath).AsTask();
            _moduleLoad = load;
            return load;
        }
    }
}
