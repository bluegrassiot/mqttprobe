using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Components.Charts;

internal sealed record ChartZoneDescription(string Visible, string Details);

internal static class ChartZoneCaption
{
    internal static ChartZoneDescription Describe(DeviceTimeFormatResult? result)
    {
        if (result is null)
            return new("", "");

        var abbreviations = new List<string>();
        var details = new List<string>();

        foreach (var item in result.Items ?? [])
        {
            var abbreviation = item.ZoneAbbreviation.Trim();
            if (abbreviation.Length == 0)
                continue;

            if (!abbreviations.Contains(abbreviation, StringComparer.Ordinal))
                abbreviations.Add(abbreviation);

            var name = string.IsNullOrWhiteSpace(item.ZoneName) ? abbreviation : item.ZoneName;
            var offset = string.IsNullOrWhiteSpace(item.OffsetLabel) ? "" : $" ({item.OffsetLabel})";
            var detail = $"{name}{offset}";
            if (!details.Contains(detail, StringComparer.Ordinal))
                details.Add(detail);
        }

        var zone = result.ZoneLabel.Trim();
        if (abbreviations.Count == 0)
        {
            if (zone.Length == 0)
                return new("", "");

            var name = string.IsNullOrWhiteSpace(result.ZoneName) ? zone : result.ZoneName;
            return new(zone, $"{zone} · {name}");
        }

        var detailsText = zone.Length == 0
            ? string.Join(" / ", details)
            : $"{zone} · {string.Join(" / ", details)}";

        return new(string.Join(" / ", abbreviations), detailsText);
    }
}
