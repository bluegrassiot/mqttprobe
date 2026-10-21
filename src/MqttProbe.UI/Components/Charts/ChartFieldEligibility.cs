using System.Globalization;

namespace MqttProbe.UI.Components.Charts;

internal static class ChartFieldEligibility
{
    internal static bool IsNumericString(string? value) =>
        value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    internal static bool IsChartableSparkplugValue(string? value, string? dataType) =>
        IsNumericString(value)
        || string.Equals(dataType, "bool", StringComparison.Ordinal) && bool.TryParse(value, out _);
}
