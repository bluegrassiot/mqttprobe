using System.Globalization;
using System.Text.Json;

namespace MqttProbe.Core.Models.Chart;

public static class ChartFieldPaths
{
    public const string Root = "$";

    public static bool TryParseRootNumber(string? payload, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var text = payload.Trim();
        if (text.StartsWith('"'))
        {
            if (!TryUnquoteJsonString(text, out var unquoted))
                return false;

            text = unquoted;
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryUnquoteJsonString(string text, out string value)
    {
        value = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.String)
                return false;

            value = doc.RootElement.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
