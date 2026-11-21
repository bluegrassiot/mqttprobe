using MqttProbe.Core.Models.Configuration;
using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

internal sealed class SparkplugTimeFormatterStub : IDeviceTimeFormatter
{
    internal sealed record Batch(IReadOnlyList<DeviceTimeFormatRequest> Requests, MessageTimePreferences Preferences);

    private readonly List<Batch> _batches = [];

    internal IReadOnlyList<Batch> Batches => _batches;

    internal string ZoneLabel { get; set; } = "Europe/London";

    internal string ZoneName { get; set; } = "British Summer Time";

    internal Func<DeviceTimeFormatRequest, MessageTimePreferences, DeviceTimeFormatItem> Format { get; set; } =
        static (request, _) => new(request.Key, $"t{request.EpochMs}", "", $"f{request.EpochMs}", "", "", "");

    internal TaskCompletionSource? FirstCallGate { get; set; }

    public async Task<DeviceTimeFormatResult?> FormatAsync(
        IReadOnlyList<DeviceTimeFormatRequest> items,
        MessageTimePreferences preferences)
    {
        _batches.Add(new Batch(items, preferences));

        if (_batches.Count == 1 && FirstCallGate is { } gate)
            await gate.Task.ConfigureAwait(false);

        return new DeviceTimeFormatResult(
            ZoneLabel,
            ZoneName,
            items.Select(item => Format(item, preferences)).ToList());
    }

    public Task<bool> EnsureBridgeAsync() => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
