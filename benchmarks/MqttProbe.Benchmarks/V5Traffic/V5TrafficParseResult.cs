namespace MqttProbe.Benchmarks.V5Traffic;

internal sealed class V5TrafficParseResult
{
    public V5TrafficOptions? Options { get; init; }
    public int ExitCode { get; init; }

    public static V5TrafficParseResult Run(V5TrafficOptions options) => new() { Options = options };
    public static V5TrafficParseResult Exit(int exitCode) => new() { ExitCode = exitCode };
}
