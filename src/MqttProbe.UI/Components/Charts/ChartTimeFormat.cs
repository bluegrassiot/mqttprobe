using MqttProbe.Core.Models.Configuration;

namespace MqttProbe.UI.Components.Charts;

internal static class ChartTimeFormat
{
    internal static (string Zone, string ClockFormat) Values(MessageTimePreferences preferences) =>
    (
        preferences.Zone == MessageTimePreferences.ZoneUtc
            ? MessageTimePreferences.ZoneUtc
            : MessageTimePreferences.ZoneLocal,
        preferences.ClockFormat == MessageTimePreferences.ClockFormat12Hour
            ? MessageTimePreferences.ClockFormat12Hour
            : MessageTimePreferences.ClockFormat24Hour
    );
}
