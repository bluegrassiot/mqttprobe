using System.Text.Json;
using MqttProbe.Core.Models.Configuration;

namespace MqttProbe.UI.Components.Charts;

internal static class ChartTimeFormat
{
    private const string Bridge = "window.mqttProbeTime";
    private const long DayMilliseconds = 86_400_000;

    internal static string Options(MessageTimePreferences preferences) =>
        JsonSerializer.Serialize(new
        {
            zone = preferences.Zone == MessageTimePreferences.ZoneUtc
                ? MessageTimePreferences.ZoneUtc
                : MessageTimePreferences.ZoneLocal,
            clockFormat = preferences.ClockFormat == MessageTimePreferences.ClockFormat12Hour
                ? MessageTimePreferences.ClockFormat12Hour
                : MessageTimePreferences.ClockFormat24Hour
        });

    internal static string AxisFormatter(MessageTimePreferences preferences) =>
        "function(value, timestamp, opts) {" +
        $"var bridge = {Bridge};" +
        "if (!bridge) return '';" +
        $"var options = {Options(preferences)};" +
        "var when = typeof timestamp === 'number' ? timestamp : value;" +
        "var time = bridge.formatAxis(when, options);" +
        "var full = bridge.formatTooltip(when, options);" +
        "var date = full.slice(0, 10);" +
        "var span = 0;" +
        "try { span = Number(opts.w.globals.maxX) - Number(opts.w.globals.minX); } catch (_) {}" +
        "var midnight = time.indexOf('00:00:00') === 0 || time.indexOf('12:00:00.000 AM') === 0;" +
        $"return span > {DayMilliseconds} || midnight ? [date, time] : time;" +
        "}";

    internal static string TooltipFormatter(MessageTimePreferences preferences) =>
        "function(value) {" +
        $"var bridge = {Bridge};" +
        "if (!bridge) return '';" +
        "var when = Array.isArray(value) ? value[0] : value;" +
        $"return bridge.formatTooltip(when, {Options(preferences)});" +
        "}";
}
