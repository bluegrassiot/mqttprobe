using System.Text;
using MQTTnet;
using MQTTnet.Protocol;

namespace MqttProbe.Benchmarks.V5Traffic;

internal static class V5TrafficScenarios
{
    public const string PayloadJson = """{"temperature":22.5,"pressure":1013.25,"flowRate":12.8,"status":"active"}""";
    public static string LongContentType { get; } =
        "application/vnd.mqttprobe." + new string('x', 256) + "+json";
    public const string WhitespaceCorrelation = "  alpha  beta\t\tgamma\r\ndelta  ";
    public const string LongCorrelationPrefix = "long-correlation:";

    private static readonly byte[] _payloadBytes = Encoding.UTF8.GetBytes(PayloadJson);
    private static readonly byte[] _binaryCorrelation = [0x00, 0xFF, 0x10, 0x80, 0xC3, 0x28, 0x01, 0xFE];

    public static IReadOnlyList<V5TrafficScenarioDefinition> All { get; } =
    [
        new()
        {
            Name = V5TrafficScenarioName.AllSix,
            CliName = "all-six",
            Summary = "ContentType, correlation, response topic, payload format, expiry and user properties",
            Build = BuildAllSix,
        },
        new()
        {
            Name = V5TrafficScenarioName.DuplicateUserProperties,
            CliName = "duplicate-user-properties",
            Summary = "Repeated user property names in publish order",
            Build = BuildDuplicateUserProperties,
        },
        new()
        {
            Name = V5TrafficScenarioName.BinaryCorrelation,
            CliName = "binary-correlation",
            Summary = "Correlation with non UTF-8 bytes",
            Build = BuildBinaryCorrelation,
        },
        new()
        {
            Name = V5TrafficScenarioName.WhitespaceCorrelation,
            CliName = "whitespace-correlation",
            Summary = "Correlation with spaces, tabs, CR and LF",
            Build = BuildWhitespaceCorrelation,
        },
        new()
        {
            Name = V5TrafficScenarioName.EmptyProperties,
            CliName = "empty-properties",
            Summary = "Zero-length correlation; empty ContentType is unsupported",
            Build = BuildEmptyProperties,
        },
        new()
        {
            Name = V5TrafficScenarioName.AbsentProperties,
            CliName = "absent-properties",
            Summary = "Payload only, no MQTT v5 properties",
            Build = BuildAbsentProperties,
        },
        new()
        {
            Name = V5TrafficScenarioName.LongContentType,
            CliName = "long-content-type",
            Summary = $"{LongContentType.Length}-byte ContentType for narrow wrapping",
            Build = BuildLongContentType,
        },
        new()
        {
            Name = V5TrafficScenarioName.UserProperties12,
            CliName = "user-properties-12",
            Summary = "12 user properties",
            Build = BuildUserProperties12,
        },
        new()
        {
            Name = V5TrafficScenarioName.UserProperties40,
            CliName = "user-properties-40",
            Summary = "40 user properties",
            Build = BuildUserProperties40,
        },
        new()
        {
            Name = V5TrafficScenarioName.LongProperties,
            CliName = "long-properties",
            Summary = "Long user property names and values plus 256-byte correlation",
            Build = BuildLongProperties,
        },
    ];

    public static IReadOnlyList<V5TrafficScenarioName> AllNames { get; } = All.Select(s => s.Name).ToArray();

    public static V5TrafficScenarioDefinition Get(V5TrafficScenarioName name) =>
        All.First(s => s.Name == name);

    public static bool TryGet(string cliName, out V5TrafficScenarioDefinition scenario)
    {
        scenario = All.FirstOrDefault(s => s.CliName.Equals(cliName, StringComparison.OrdinalIgnoreCase))!;
        return scenario is not null;
    }

    private static MqttApplicationMessage BuildAllSix(string prefix)
    {
        var builder = CreateBuilder(prefix, "all-six")
            .WithContentType("application/json")
            .WithCorrelationData(Encoding.UTF8.GetBytes("all-six-correlation"))
            .WithResponseTopic($"{prefix.TrimEnd('/')}/all-six/reply")
            .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.CharacterData)
            .WithMessageExpiryInterval(60);

        builder = AddUserProperty(builder, "source", "mqttprobe");
        builder = AddUserProperty(builder, "suite", "v5-traffic");
        return builder.Build();
    }

    private static MqttApplicationMessage BuildDuplicateUserProperties(string prefix)
    {
        var builder = CreateBuilder(prefix, "duplicate-user-properties");
        builder = AddUserProperty(builder, "trace", "first");
        builder = AddUserProperty(builder, "trace", "second");
        builder = AddUserProperty(builder, "trace", "third");
        builder = AddUserProperty(builder, "region", "eu-west");
        builder = AddUserProperty(builder, "region", "us-east");
        builder = AddUserProperty(builder, "sample", "alpha");
        return builder.Build();
    }

    private static MqttApplicationMessage BuildBinaryCorrelation(string prefix) =>
        CreateBuilder(prefix, "binary-correlation")
            .WithCorrelationData(_binaryCorrelation)
            .Build();

    private static MqttApplicationMessage BuildWhitespaceCorrelation(string prefix) =>
        CreateBuilder(prefix, "whitespace-correlation")
            .WithCorrelationData(Encoding.UTF8.GetBytes(WhitespaceCorrelation))
            .Build();

    private static MqttApplicationMessage BuildEmptyProperties(string prefix) =>
        CreateBuilder(prefix, "empty-properties")
            .WithCorrelationData([])
            .WithContentType("")
            .Build();

    private static MqttApplicationMessage BuildAbsentProperties(string prefix) =>
        CreateBuilder(prefix, "absent-properties").Build();

    private static MqttApplicationMessage BuildLongContentType(string prefix) =>
        CreateBuilder(prefix, "long-content-type")
            .WithContentType(LongContentType)
            .Build();

    private static MqttApplicationMessage BuildUserProperties12(string prefix)
    {
        var builder = CreateBuilder(prefix, "user-properties-12");
        for (var i = 1; i <= 12; i++)
            builder = AddUserProperty(builder, $"metric-{i:00}", $"value-{i:00}");
        return builder.Build();
    }

    private static MqttApplicationMessage BuildUserProperties40(string prefix)
    {
        var builder = CreateBuilder(prefix, "user-properties-40");
        for (var i = 1; i <= 40; i++)
            builder = AddUserProperty(builder, $"metric-{i:00}", $"value-{i:00}");
        return builder.Build();
    }

    private static MqttApplicationMessage BuildLongProperties(string prefix)
    {
        var builder = CreateBuilder(prefix, "long-properties")
            .WithCorrelationData(Encoding.UTF8.GetBytes(LongCorrelationPrefix + new string('c', 239)));
        builder = AddUserProperty(builder, "acceptance-long-property-name-01-" + new string('n', 48),
            "value-01-" + new string('v', 120));
        builder = AddUserProperty(builder, "acceptance-long-property-name-02-" + new string('m', 48),
            "value-02-" + new string('w', 120));
        builder = AddUserProperty(builder, "acceptance-long-property-name-03-" + new string('o', 48),
            "value-03-" + new string('u', 120));
        return builder.Build();
    }

    private static MqttApplicationMessageBuilder CreateBuilder(string prefix, string scenario) =>
        new MqttApplicationMessageBuilder()
            .WithTopic($"{prefix.TrimEnd('/')}/{scenario}")
            .WithPayload(_payloadBytes)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false);

    private static MqttApplicationMessageBuilder AddUserProperty(
        MqttApplicationMessageBuilder builder, string name, string value) =>
        builder.WithUserProperty(name, Encoding.UTF8.GetBytes(value).AsMemory());
}
