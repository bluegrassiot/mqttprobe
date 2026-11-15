using System.Text.Json.Serialization;

namespace MqttProbe.UI.Components.Charts;

internal sealed record ChartExportSeriesSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("pointCount")] int PointCount);

internal sealed record ChartExportSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("clockFormat")] string ClockFormat,
    [property: JsonPropertyName("series")] List<ChartExportSeriesSummary> Series);
