extern alias benchmarks;

using System.Reflection;
using benchmarks::MqttProbe.Benchmarks.V5Traffic;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
[NonParallelizable]
public class V5TrafficCommandTests
{
    private static readonly Type _commandType = typeof(V5TrafficRunner).Assembly.GetType(
        "MqttProbe.Benchmarks.V5Traffic.V5TrafficCommand", throwOnError: true)!;

    private static readonly MethodInfo _runMethod = _commandType.GetMethod(
        "RunAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(_commandType.FullName, "RunAsync");

    private static readonly Type _redactionType = typeof(V5TrafficRunner).Assembly.GetType(
        "MqttProbe.Benchmarks.V5Traffic.V5TrafficRedaction", throwOnError: true)!;

    private static readonly MethodInfo _redactMethod = _redactionType.GetMethod(
        "Redact", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(_redactionType.FullName, "Redact");

    private static readonly string[] _environmentNames =
    [
        "MQTT_BENCHMARK_HOST",
        "MQTT_BENCHMARK_PORT",
        "MQTT_BENCHMARK_USER",
        "MQTT_BENCHMARK_PASSWORD",
        "MQTT_BENCHMARK_CA_PASSWORD",
        "MQTT_BENCHMARK_CLIENT_CERT_PASSWORD",
    ];

    private readonly Dictionary<string, string?> _savedEnvironment = [];

    [OneTimeSetUp]
    public void SetUp()
    {
        foreach (var name in _environmentNames)
        {
            _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        foreach (var (name, value) in _savedEnvironment)
            Environment.SetEnvironmentVariable(name, value);
    }

    [Test]
    public async Task Command_RejectsOptionTokenAsUsernameValue()
    {
        var result = await RunCommandAsync(
            "--host", "127.0.0.1",
            "--port", "1",
            "--username", "--tls");

        result.ExitCode.Should().Be(1);
        result.StandardError.Should().Contain("--username");
    }

    [Test]
    public void Redaction_HidesAllCredentialValues()
    {
        var options = new V5TrafficOptions
        {
            Username = "api-user",
            Password = "api-password",
            CaCertificatePassword = "ca-password",
            ClientCertificatePassword = "client-password",
        };
        var text = "api-user api-password ca-password client-password " +
                   "mqtt://api-user:api-password@broker.example";

        var redacted = (string)_redactMethod.Invoke(null, [text, options])!;

        redacted.Should().NotContain("api-user");
        redacted.Should().NotContain("api-password");
        redacted.Should().NotContain("ca-password");
        redacted.Should().NotContain("client-password");
        redacted.Should().Contain("mqtt://***:***@broker.example");
    }

    [Test]
    public async Task Command_DoesNotEchoUserInfoHost()
    {
        var result = await RunCommandAsync(
            "--host", "mqtt://user:supersecret@127.0.0.1",
            "--port", "1",
            "--scenario", "absent-properties",
            "--interval", "0");

        result.ExitCode.Should().Be(1);
        result.StandardOutput.Should().NotContain("supersecret");
        result.StandardError.Should().NotContain("supersecret");
        result.StandardError.Should().NotContain("user:supersecret");
    }

    private static async Task<CommandResult> RunCommandAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var task = (Task<int>)_runMethod.Invoke(null, [args])!;
            return new CommandResult(await task, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
