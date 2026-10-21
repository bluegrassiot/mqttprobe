using MqttProbe.Core.Models.Chart;

namespace MqttProbe.UI.Components.Sparkplug;

public sealed record SparkplugChartSelection(string Topic, ChartFieldSelection Field);
