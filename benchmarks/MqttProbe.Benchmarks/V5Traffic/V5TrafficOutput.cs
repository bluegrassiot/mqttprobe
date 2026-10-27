namespace MqttProbe.Benchmarks.V5Traffic;

internal sealed class V5TrafficOutput(V5TrafficOptions options)
{
    public void Line(string value) =>
        Console.WriteLine(V5TrafficRedaction.Redact(value, options));

    public void Error(string value) =>
        Console.Error.WriteLine(V5TrafficRedaction.Redact(value, options));

    public string Exception(Exception exception) =>
        V5TrafficRedaction.DescribeException(exception, options);
}
