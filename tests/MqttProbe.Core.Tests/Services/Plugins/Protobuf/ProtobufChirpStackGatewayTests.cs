using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MqttProbe.Core.Models.Plugins;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins;
using MqttProbe.Core.Services.Plugins.Packaging;
using MqttProbe.Core.Services.Plugins.Protobuf;
using static MqttProbe.Core.Tests.Services.Plugins.Protobuf.ChirpStackSampleFixture;

namespace MqttProbe.Core.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class ProtobufChirpStackGatewayTests
{
    private static readonly (string Topic, string MessageType, string Fixture) _connState =
        ("us915_1/gateway/008000000001fdd9/state/conn", "ConnState", "state-conn.b64");

    private static readonly (string Topic, string MessageType, string Fixture)[] _fixtures =
    [
        ("us915_1/gateway/008000000001fdbd/event/up", "UplinkFrame", "event-up.b64"),
        ("us915_1/gateway/008000000001fdbd/event/stats", "GatewayStats", "event-stats.b64"),
        ("us915_1/gateway/008000000001fdbd/event/ack", "DownlinkTxAck", "event-ack.b64"),
        ("us915_1/gateway/008000000001fdbd/command/down", "DownlinkFrame", "command-down.b64"),
        _connState,
    ];

    private static string _pluginFolder = string.Empty;
    private static ProtobufSchemaRegistry _registry = null!;
    private static ProtobufPayloadDecoder _decoder = null!;
    private static ProtobufPayloadDetector _detector = null!;

    [OneTimeSetUp]
    public void StageSchemaSet()
    {
        _pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-gw-" + Guid.NewGuid().ToString("N"));
        var schemaRoot = Path.Combine(_pluginFolder, ProtobufSchemaFolderLoader.FolderName, "chirpstack");
        CopyDirectory(ChirpStackSampleDir(), schemaRoot);

        var sources = ProtobufSchemaFolderLoader.Discover([_pluginFolder]);
        sources.Should().ContainSingle();

        _registry = new ProtobufSchemaRegistry(sources);
        ChirpStackSampleDiagnostics.ShouldMatchKnownWarnings(_registry);
        _decoder = new ProtobufPayloadDecoder(_registry);
        _detector = new ProtobufPayloadDetector(_registry);
    }

    private static string FixtureDir() =>
        Path.Combine(RepoRoot(), "tests", "MqttProbe.Core.Tests", "Services", "Plugins", "Protobuf", "Fixtures");

    private static byte[] FixtureBytes(string fixture) =>
        Convert.FromBase64String(File.ReadAllText(Path.Combine(FixtureDir(), fixture)).Trim());

    [OneTimeTearDown]
    public void RemoveSchemaSet()
    {
        if (Directory.Exists(_pluginFolder))
            Directory.Delete(_pluginFolder, recursive: true);
    }

    private static MqttApplicationMessageReceivedEventArgs GatewayArgs(string fixture) =>
        MakeArgs(_fixtures.Single(f => f.Fixture == fixture).Topic, FixtureBytes(fixture));

    private static JsonElement Decode(string fixture) =>
        DecodeWith(_decoder, fixture);

    private static JsonElement DecodeWith(ProtobufPayloadDecoder decoder, string fixture)
    {
        var envelope = decoder.Decode(GatewayArgs(fixture));
        envelope.IsFailure.Should().BeFalse(envelope.FailureReason);

        using var doc = JsonDocument.Parse(envelope.DisplayText);
        return doc.RootElement.Clone();
    }

    private static IEnumerable<TestCaseData> FixtureCases()
    {
        foreach (var fixture in _fixtures)
            yield return new TestCaseData(fixture.Topic, fixture.MessageType, fixture.Fixture);
    }

    private static IEnumerable<TestCaseData> FixtureNameCases()
    {
        foreach (var fixture in _fixtures)
            yield return new TestCaseData(fixture.Fixture);
    }

    [TestCaseSource(nameof(FixtureCases))]
    public void Gateway_Topic_Routes_To_Its_Message_And_Is_Detected(string topic, string messageType, string fixture)
    {
        _registry.TryResolveByTopic(topic, out var message).Should().BeTrue();
        message.Name.Should().Be(messageType);
        _detector.CanDetect(GatewayArgs(fixture)).Should().BeTrue();
    }

    [TestCaseSource(nameof(FixtureNameCases))]
    public void Registered_Detector_Chain_Claims_Gateway_Topics_As_Protobuf(string fixture)
    {
        var config = new PluginConfig { PluginFolders = { _pluginFolder } };
        var registry = MqttProbePluginStartup.BuildPluginRegistry(config, NullLoggerFactory.Instance);

        registry.FindDetector(GatewayArgs(fixture))!.FormatId.Should().Be("protobuf");
    }

    [Test]
    public void Decodes_Uplink_Frame()
    {
        var root = Decode("event-up.b64");

        root.GetProperty("phy_payload").GetString().Should().Be("QDTnpwGDdTIG/gcCvZO4R1Wyd+YxNGdfkoSo");

        var txInfo = root.GetProperty("tx_info");
        txInfo.GetProperty("frequency").GetUInt32().Should().Be(903_900_000);
        var txLora = txInfo.GetProperty("modulation").GetProperty("lora");
        txLora.GetProperty("bandwidth").GetUInt32().Should().Be(125_000);
        txLora.GetProperty("spreading_factor").GetUInt32().Should().Be(7);
        txLora.GetProperty("code_rate").GetString().Should().Be("CR_4_5");

        var rxInfo = root.GetProperty("rx_info");
        rxInfo.GetProperty("gateway_id").GetString().Should().Be("008000000001fdbd");
        rxInfo.GetProperty("uplink_id").GetUInt32().Should().Be(9790);
        rxInfo.GetProperty("rssi").GetInt32().Should().Be(-31);
        rxInfo.GetProperty("snr").GetSingle().Should().BeApproximately(10.2f, 0.001f);
        rxInfo.GetProperty("context").GetString().Should().Be("rg3fOw==");
        rxInfo.GetProperty("crc_status").GetString().Should().Be("CRC_OK");
    }

    [Test]
    public void Decodes_Gateway_Stats()
    {
        var root = Decode("event-stats.b64");

        root.GetProperty("time").GetProperty("seconds").GetInt64().Should().Be(1_630_573_282);
        root.GetProperty("gateway_id").GetString().Should().Be("008000000001fdbd");
        root.GetProperty("rx_packets_received").GetUInt32().Should().Be(1);
        root.GetProperty("rx_packets_received_ok").GetUInt32().Should().Be(1);
        root.GetProperty("tx_packets_received").GetUInt32().Should().Be(1);
        root.GetProperty("tx_packets_emitted").GetUInt32().Should().Be(1);

        var txFrequencies = root.GetProperty("tx_packets_per_frequency");
        txFrequencies.EnumerateArray().Should().ContainSingle();
        txFrequencies[0].GetProperty("key").GetUInt32().Should().Be(927_500_000);
        txFrequencies[0].GetProperty("value").GetUInt32().Should().Be(1);

        var rxFrequencies = root.GetProperty("rx_packets_per_frequency");
        rxFrequencies.EnumerateArray().Should().ContainSingle();
        rxFrequencies[0].GetProperty("key").GetUInt32().Should().Be(905_300_000);

        var statuses = root.GetProperty("tx_packets_per_status");
        statuses.EnumerateArray().Should().ContainSingle();
        statuses[0].GetProperty("key").GetString().Should().Be("OK");
        statuses[0].GetProperty("value").GetUInt32().Should().Be(1);

        var txModulation = root.GetProperty("tx_packets_per_modulation")[0];
        txModulation.GetProperty("count").GetUInt32().Should().Be(1);
        txModulation.GetProperty("modulation").GetProperty("lora")
            .GetProperty("bandwidth").GetUInt32().Should().Be(500_000);

        var rxModulation = root.GetProperty("rx_packets_per_modulation")[0];
        rxModulation.GetProperty("modulation").GetProperty("lora")
            .GetProperty("bandwidth").GetUInt32().Should().Be(125_000);
    }

    [Test]
    public void Decodes_Downlink_TxAck()
    {
        var root = Decode("event-ack.b64");

        root.GetProperty("downlink_id").GetUInt32().Should().Be(865_876_675);
        root.GetProperty("gateway_id").GetString().Should().Be("008000000001fdbd");

        var items = root.GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("status").GetString().Should().Be("OK");
        items[1].EnumerateObject().Should().BeEmpty();
    }

    [Test]
    public void Decodes_Downlink_Frame_With_Legacy_And_Current_Tx_Info()
    {
        var root = Decode("command-down.b64");

        root.GetProperty("downlink_id").GetUInt32().Should().Be(708_599_062);
        root.GetProperty("downlink_id_legacy").GetString().Should().Be("AAAAAAAAAAAAAAAAKjxdFg==");
        root.GetProperty("gateway_id").GetString().Should().Be("008000000001fdbd");
        root.GetProperty("gateway_id_legacy").GetString().Should().Be("AIAAAAAB/b0=");

        var items = root.GetProperty("items");
        items.GetArrayLength().Should().Be(2);

        var first = items[0];
        first.GetProperty("phy_payload").GetString().Should().Be("YDJvyAGB1oIGkMoHvw==");
        first.GetProperty("tx_info").GetProperty("frequency").GetUInt32().Should().Be(925_100_000);
        first.GetProperty("tx_info").GetProperty("power").GetInt32().Should().Be(20);
        first.GetProperty("tx_info").GetProperty("modulation").GetProperty("lora")
            .GetProperty("bandwidth").GetUInt32().Should().Be(500_000);
        first.GetProperty("tx_info").GetProperty("timing").GetProperty("delay")
            .GetProperty("delay").GetProperty("seconds").GetInt64().Should().Be(1);

        var legacy = first.GetProperty("tx_info_legacy");
        legacy.GetProperty("frequency").GetUInt32().Should().Be(925_100_000);
        legacy.GetProperty("timing").GetString().Should().Be("DELAY");
        legacy.GetProperty("lora_modulation_info").GetProperty("bandwidth").GetUInt32().Should().Be(500);
        legacy.GetProperty("lora_modulation_info").GetProperty("code_rate_legacy").GetString().Should().Be("4/5");
        legacy.GetProperty("delay_timing_info").GetProperty("delay").GetProperty("seconds").GetInt64().Should().Be(1);

        var second = items[1];
        second.GetProperty("tx_info").GetProperty("frequency").GetUInt32().Should().Be(923_300_000);
        second.GetProperty("tx_info").GetProperty("modulation").GetProperty("lora")
            .GetProperty("spreading_factor").GetUInt32().Should().Be(12);
        second.GetProperty("tx_info").GetProperty("timing").GetProperty("delay")
            .GetProperty("delay").GetProperty("seconds").GetInt64().Should().Be(2);
    }

    [TestCaseSource(nameof(FixtureNameCases))]
    public void Decode_Leaves_No_Unknown_Fields(string fixture)
    {
        AssertNoUnknownFields(Decode(fixture));
    }

    [Test]
    public void Decodes_Reported_Connection_State()
    {
        FixtureBytes(_connState.Fixture).Length.Should().Be(20);

        _registry.TryResolveByTopic(_connState.Topic, out var message).Should().BeTrue();
        message.Name.Should().Be(_connState.MessageType);

        var root = Decode(_connState.Fixture);
        AssertConnState(root);
        AssertNoUnknownFields(root);
    }

    private static void AssertConnState(JsonElement root)
    {
        root.GetProperty("state").GetString().Should().Be("ONLINE");
        root.GetProperty("gateway_id").GetString().Should().Be("008000000001fdd9");
    }

    [Test]
    public async Task Installed_Package_Decodes_Every_Gateway_Fixture()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-gw-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginFolder);

        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.3");
        var config = new PluginConfig();
        config.PluginFolders.Add(pluginFolder);

        var installer = new PluginPackageInstaller(
            config, appInfo, new PluginInstallSession(), new PluginArchiveLimits(), NullLoggerFactory.Instance);

        var outcome = await installer.InstallAsync(PackSampleAsPackage(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Manifest!.Version.Should().Be("1.0.2");

        var registry = new ProtobufSchemaRegistry(ProtobufSchemaFolderLoader.Discover([pluginFolder]));
        ChirpStackSampleDiagnostics.ShouldMatchKnownWarnings(registry);
        var decoder = new ProtobufPayloadDecoder(registry);

        foreach (var (_, _, fixture) in _fixtures)
            AssertNoUnknownFields(DecodeWith(decoder, fixture));

        var connState = DecodeWith(decoder, _connState.Fixture);
        AssertConnState(connState);
        AssertNoUnknownFields(connState);

        Directory.Delete(pluginFolder, recursive: true);
    }

    private static void AssertNoUnknownFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                property.Name.Should().NotStartWith("field_");
                AssertNoUnknownFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertNoUnknownFields(item);
        }
    }
}
