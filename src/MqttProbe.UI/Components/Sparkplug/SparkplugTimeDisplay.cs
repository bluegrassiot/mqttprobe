using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Components.Sparkplug;

public sealed class SparkplugTimeDisplay
{
    internal const string PendingMarker = "…";
    internal const string PendingTitle = "Waiting for the viewing device clock";

    internal SparkplugTimeDisplay(string ianaZone, IReadOnlyDictionary<string, DeviceTimeFormatItem> items)
    {
        IanaZone = ianaZone;
        Items = items;
    }

    internal static SparkplugTimeDisplay Empty { get; } =
        new("", new Dictionary<string, DeviceTimeFormatItem>(StringComparer.Ordinal));

    internal SparkplugZoneDetail? ZoneCaption => ZoneFor(SparkplugTimeKeys.RegionCaption);

    private string IanaZone { get; }

    private IReadOnlyDictionary<string, DeviceTimeFormatItem> Items { get; }

    internal DeviceTimeFormatItem? Find(string key) =>
        Items.TryGetValue(key, out var item) ? item : null;

    internal SparkplugZoneDetail? ZoneFor(string key) =>
        Find(key) is { ZoneAbbreviation.Length: > 0 } item
            ? new SparkplugZoneDetail(item.ZoneAbbreviation, IanaZone, item.ZoneName, item.OffsetLabel)
            : null;
}

internal sealed record SparkplugZoneDetail(string Abbreviation, string IanaZone, string ZoneName, string OffsetLabel)
{
    internal string Tooltip => string.Join(" · ", new[] { IanaZone, ZoneName, OffsetLabel }.Where(Present));

    internal string AccessibleLabel => $"{Abbreviation}: {Tooltip}";

    private static bool Present(string value) => !string.IsNullOrWhiteSpace(value);
}

internal static class SparkplugTimeKeys
{
    internal const string RegionCaption = "region:caption";
    internal const string NodeBirth = "node:birth";
    internal const string NodeLastData = "node:last-data";
    internal const string NodeDeath = "node:death";
    internal const string NodeRebirthRequest = "node:rebirth-request";

    internal static string Metric(string topic, string name) => $"metric|{topic}|{name}";
}
