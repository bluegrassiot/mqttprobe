using System.Text.Json.Serialization;

namespace MqttProbe.UI.Components.Charts;

internal sealed record ChartRendererSpec(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("dark")] bool Dark,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("clockFormat")] string ClockFormat,
    [property: JsonPropertyName("fontProfile")] string FontProfile,
    [property: JsonPropertyName("series")] List<ChartRendererSeries> Series);

internal sealed record ChartRendererSeries(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("points")] List<double[]> Points);
