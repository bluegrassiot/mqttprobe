using MqttProbe.Core.Models.Configuration;
using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Tests.Components.Charts;

internal sealed class ChartTimeFormatterStub : IDeviceTimeFormatter
{
    internal bool BridgeReady { get; set; } = true;

    internal string ZoneLabel { get; set; } = "UTC";

    internal string ZoneName { get; set; } = "Coordinated Universal Time";

    internal string Abbreviation { get; set; } = "UTC";

    internal string OffsetLabel { get; set; } = "UTC+00:00";

    internal Func<long, (string Abbreviation, string ZoneName, string OffsetLabel)>? ItemFor { get; set; }

    internal int FailuresRemaining { get; set; }

    internal List<MessageTimePreferences> Requests { get; } = [];

    public Task<DeviceTimeFormatResult?> FormatAsync(
        IReadOnlyList<DeviceTimeFormatRequest> items,
        MessageTimePreferences preferences)
    {
        Requests.Add(preferences);
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            return Task.FromResult<DeviceTimeFormatResult?>(null);
        }

        var formatted = new List<DeviceTimeFormatItem>(items.Count);
        foreach (var request in items)
        {
            var (abbreviation, zoneName, offset) = ItemFor?.Invoke(request.EpochMs)
                ?? (Abbreviation, ZoneName, OffsetLabel);
            formatted.Add(new DeviceTimeFormatItem(
                request.Key, "", "", "", abbreviation, zoneName, offset));
        }

        return Task.FromResult<DeviceTimeFormatResult?>(
            new DeviceTimeFormatResult(ZoneLabel, ZoneName, formatted));
    }

    public Task<bool> EnsureBridgeAsync() => Task.FromResult(BridgeReady);

    public ValueTask DisposeAsync() => default;
}
