using System.Text.Json;
using Google.Protobuf;
using MQTTnet;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class ProtobufPayloadDecoderTests
{
    private const string Proto = """
        syntax = "proto3";
        package demo;
        message Reading { int32 id = 1; string name = 2; }
        """;

    private static ProtobufSchemaRegistry BuildRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mqttprobe-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "demo.proto"), Proto);
        var config = new ProtobufSchemaManifest
        {
            Schemas = { new ProtobufSchemaMapping { Files = { "demo.proto" }, TopicPattern = "sensors/+/data", MessageType = "demo.Reading" } }
        };
        return new ProtobufSchemaRegistry(config, dir);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static byte[] EncodeReading(int id, string name)
    {
        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);
        cos.WriteTag(1, WireFormat.WireType.Varint); cos.WriteInt32(id);
        cos.WriteTag(2, WireFormat.WireType.LengthDelimited); cos.WriteString(name);
        cos.Flush();
        return ms.ToArray();
    }

    [Test]
    public void Detector_Matches_Configured_Topic_Only()
    {
        var registry = BuildRegistry();
        var detector = new ProtobufPayloadDetector(registry);

        detector.CanDetect(MakeArgs("sensors/1/data", EncodeReading(1, "a"))).Should().BeTrue();
        detector.CanDetect(MakeArgs("sensors/1/other", EncodeReading(1, "a"))).Should().BeFalse();
    }

    [Test]
    public void Detector_Priority_Sits_Below_Sparkplug_And_Above_MessagePack()
    {
        new ProtobufPayloadDetector(BuildRegistry()).Priority.Should().Be(850);
    }

    // Sparkplug owns spBv1.0 by spec. Even a catch-all user pattern must not claim it,
    // or SparkplugTopologyExtractor loses the typed Payload and topology breaks.
    [Test]
    public void Detector_Never_Claims_Sparkplug_Namespace_Even_With_CatchAll_Pattern()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mqttprobe-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "demo.proto"), Proto);
        var config = new ProtobufSchemaManifest
        {
            // Deliberately greedy: '#' matches everything, including spBv1.0/...
            Schemas = { new ProtobufSchemaMapping { Files = { "demo.proto" }, TopicPattern = "#", MessageType = "demo.Reading" } }
        };
        var detector = new ProtobufPayloadDetector(new ProtobufSchemaRegistry(config, dir));

        detector.CanDetect(MakeArgs("spBv1.0/group/NBIRTH/node", EncodeReading(1, "a")))
            .Should().BeFalse("Sparkplug B must win deterministically in its own namespace");
        detector.CanDetect(MakeArgs("anything/else", EncodeReading(1, "a")))
            .Should().BeTrue("the catch-all pattern still applies outside spBv1.0");
    }

    [Test]
    public void Decoder_Produces_Json_DisplayText()
    {
        var registry = BuildRegistry();
        var decoder = new ProtobufPayloadDecoder(registry);

        var envelope = decoder.Decode(MakeArgs("sensors/1/data", EncodeReading(7, "dev")));

        envelope.IsFailure.Should().BeFalse();
        envelope.FormatId.Should().Be("protobuf");
        using var doc = JsonDocument.Parse(envelope.DisplayText);
        doc.RootElement.GetProperty("id").GetInt64().Should().Be(7);
        doc.RootElement.GetProperty("name").GetString().Should().Be("dev");
    }

    [Test]
    public void Decoder_Empty_Payload_Is_Success_Empty()
    {
        var registry = BuildRegistry();
        var decoder = new ProtobufPayloadDecoder(registry);

        var envelope = decoder.Decode(MakeArgs("sensors/1/data", []));

        envelope.IsFailure.Should().BeFalse();
        envelope.DisplayText.Should().BeEmpty();
    }

    [Test]
    public void Decoder_Malformed_Payload_Is_Failure_Not_Exception()
    {
        var registry = BuildRegistry();
        var decoder = new ProtobufPayloadDecoder(registry);
        byte[] malformed = [0x72, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F];

        var envelope = decoder.Decode(MakeArgs("sensors/1/data", malformed));

        envelope.IsFailure.Should().BeTrue();
        envelope.FailureReason.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void Decoder_Float_NaN_Serializes_Without_Failure()
    {
        const string proto = """
            syntax = "proto3";
            package demo;
            message Reading { float value = 1; }
            """;
        var dir = Path.Combine(Path.GetTempPath(), "mqttprobe-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "demo.proto"), proto);
        var config = new ProtobufSchemaManifest
        {
            Schemas = { new ProtobufSchemaMapping { Files = { "demo.proto" }, TopicPattern = "sensors/+/data", MessageType = "demo.Reading" } }
        };
        var registry = new ProtobufSchemaRegistry(config, dir);
        var decoder = new ProtobufPayloadDecoder(registry);

        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);
        cos.WriteTag(1, WireFormat.WireType.Fixed32);
        cos.WriteFloat(float.NaN);
        cos.Flush();

        var envelope = decoder.Decode(MakeArgs("sensors/1/data", ms.ToArray()));

        envelope.IsFailure.Should().BeFalse();
        var act = () => JsonDocument.Parse(envelope.DisplayText);
        act.Should().NotThrow();
    }
}

[TestFixture]
public class ProtobufStartupRegistrationTests
{
    private static PluginConfig ConfigWithDroppedInSchema()
    {
        var pluginFolder = Path.Combine(Path.GetTempPath(), "mqttprobe-plugins-" + Guid.NewGuid().ToString("N"));
        var schemaRoot = Path.Combine(pluginFolder, ProtobufSchemaFolderLoader.FolderName);
        Directory.CreateDirectory(schemaRoot);

        File.WriteAllText(Path.Combine(schemaRoot, "demo.proto"),
            "syntax = \"proto3\"; package demo; message Reading { int32 id = 1; }");
        File.WriteAllText(Path.Combine(schemaRoot, ProtobufSchemaFolderLoader.ManifestFileName), """
            {
              "schemas": [
                { "files": [ "demo.proto" ], "topicPattern": "sensors/+/data", "messageType": "demo.Reading" }
              ]
            }
            """);

        return new PluginConfig { PluginFolders = { pluginFolder } };
    }

    [Test]
    public void Protobuf_Decoder_Registered_When_Schema_Folder_Dropped_In()
    {
        var registry = MqttProbe.Services.Plugins.MqttProbePluginStartup.BuildPluginRegistry(
            ConfigWithDroppedInSchema(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        registry.FindDecoder("protobuf").Should().NotBeNull();
    }

    [Test]
    public void Protobuf_Decoder_Absent_When_No_Schema_Folder()
    {
        var registry = MqttProbe.Services.Plugins.MqttProbePluginStartup.BuildPluginRegistry(
            new PluginConfig(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        registry.FindDecoder("protobuf").Should().BeNull();
    }
}
