using MQTTnet;

namespace MqttProbe.Benchmarks.V5Traffic;

internal sealed class V5TrafficScenarioDefinition
{
    public required V5TrafficScenarioName Name { get; init; }
    public required string CliName { get; init; }
    public required string Summary { get; init; }
    public required Func<string, MqttApplicationMessage> Build { get; init; }
}
