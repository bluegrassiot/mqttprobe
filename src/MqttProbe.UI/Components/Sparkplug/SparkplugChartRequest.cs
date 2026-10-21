namespace MqttProbe.UI.Components.Sparkplug;

public sealed record SparkplugChartRequest(string Topic, string JsonPath, string? SeriesName);
