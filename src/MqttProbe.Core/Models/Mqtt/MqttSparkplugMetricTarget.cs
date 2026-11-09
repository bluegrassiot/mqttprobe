namespace MqttProbe.Core.Models.Mqtt;

public sealed record MqttSparkplugMetricTarget(SparkplugSelectionScope Scope, string MetricName);
