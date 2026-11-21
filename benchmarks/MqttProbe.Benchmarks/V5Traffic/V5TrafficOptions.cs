namespace MqttProbe.Benchmarks.V5Traffic;

public sealed class V5TrafficOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 1883;
    public string Prefix { get; init; } = "benchmarks/v5";

    public bool UseTls { get; init; }
    public bool AllowUntrustedCertificate { get; init; }
    public string? CaCertificatePath { get; init; }
    public string? CaCertificatePassword { get; init; }
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificatePassword { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }

    public IReadOnlyList<V5TrafficScenarioName> Scenarios { get; init; } = V5TrafficScenarios.AllNames;
    public int Repeat { get; init; } = 1;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
}
