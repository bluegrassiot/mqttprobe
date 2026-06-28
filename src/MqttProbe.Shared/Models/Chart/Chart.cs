using System.Text.Json.Serialization;

namespace MqttProbe.Models.Chart;

public enum ChartType { Line, Area, Bar }

public static class ChartPalette
{
    // Categorical data-viz palette, deliberately distinct from the four Signal colors —
    // those carry fixed system meaning and must never appear as decorative data series.
    public static readonly string[] Series =
        ["#F97316", "#A78BFA", "#2DD4BF", "#F472B6", "#818CF8", "#22D3EE"];
}

public class ChartSeries
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";
    public string Topic { get; set; } = "";
    public string JsonPath { get; set; } = "";
    public string Color { get; set; } = ChartPalette.Series[0];
}

public class ChartConfiguration
{
    private const int DefaultMaxPoints = 500;
    private int _maxPoints = DefaultMaxPoints;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Chart";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChartType Type { get; set; } = ChartType.Line;

    public int MaxPoints
    {
        get => _maxPoints;
        set => _maxPoints = value > 0 ? value : DefaultMaxPoints;
    }

    private int? _timeWindowMinutes;

    public int? TimeWindowMinutes
    {
        get => _timeWindowMinutes;
        set => _timeWindowMinutes = value is > 0 ? value : null;
    }

    public List<ChartSeries> Series { get; set; } = [];
}

public record ChartDataPoint(DateTime Timestamp, double Value);

public record ChartFieldSelection(string JsonPath, string? SeriesName = null);

public class DiscoveredField
{
    public string Topic { get; set; } = "";
    public string JsonPath { get; set; } = "";
    public double LastValue { get; set; }
    public DateTime LastSeen { get; set; }
    public string? ContextJson { get; set; }
}
