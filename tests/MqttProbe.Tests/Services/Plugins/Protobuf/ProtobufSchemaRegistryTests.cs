using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class ProtobufSchemaRegistryTests
{
    private static string WriteProtos(params (string name, string body)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mqttprobe-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files)
        {
            var full = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }
        return dir;
    }

    private const string SampleProto = """
        syntax = "proto3";
        package demo;
        enum Color { UNKNOWN = 0; RED = 1; GREEN = 2; }
        message Inner { string label = 1; }
        message Outer {
          int32 id = 1;
          Inner inner = 2;
          Color color = 3;
          repeated int32 values = 4;
        }
        """;

    // Two vendors each shipping their own common/common.proto is the case that motivated
    // per-source import roots: with a shared FileDescriptorSet, one silently shadows the other.
    [Test]
    public void Separate_Schema_Sets_Do_Not_Shadow_Each_Others_Imports()
    {
        var chirpLike = WriteProtos(
            ("common/common.proto", """
                syntax = "proto3";
                package common;
                message Location { double latitude = 1; }
                """),
            ("integration/uplink.proto", """
                syntax = "proto3";
                package integration;
                import "common/common.proto";
                message UplinkEvent { common.Location location = 1; }
                """));

        var acmeLike = WriteProtos(
            ("common/common.proto", """
                syntax = "proto3";
                package acme;
                message Location { string site_code = 1; }
                """),
            ("telemetry/telemetry.proto", """
                syntax = "proto3";
                package acme;
                import "common/common.proto";
                message Telemetry { acme.Location location = 1; }
                """));

        var registry = new ProtobufSchemaRegistry([
            new ProtobufSchemaSource(
                new ProtobufSchemaManifest
                {
                    Schemas = { new ProtobufSchemaMapping
                    {
                        Files = { "integration/uplink.proto" },
                        TopicPattern = "chirp/#",
                        MessageType = "integration.UplinkEvent"
                    } }
                }, chirpLike),
            new ProtobufSchemaSource(
                new ProtobufSchemaManifest
                {
                    Schemas = { new ProtobufSchemaMapping
                    {
                        Files = { "telemetry/telemetry.proto" },
                        TopicPattern = "acme/#",
                        MessageType = "acme.Telemetry"
                    } }
                }, acmeLike)
        ]);

        registry.Diagnostics.Should().NotContain(d => d.StartsWith("ERROR"),
            "each set must resolve its own common/common.proto");
        registry.TryResolveByTopic("chirp/x", out var uplink).Should().BeTrue();
        uplink.Name.Should().Be("UplinkEvent");
        registry.TryResolveByTopic("acme/x", out var telemetry).Should().BeTrue();
        telemetry.Name.Should().Be("Telemetry");

        // Distinct packages, so both Location types survive with their own fields.
        registry.TryResolveMessage(".common.Location", out var commonLocation).Should().BeTrue();
        commonLocation.Fields[0].Name.Should().Be("latitude");
        registry.TryResolveMessage(".acme.Location", out var acmeLocation).Should().BeTrue();
        acmeLocation.Fields[0].Name.Should().Be("site_code");
    }

    [Test]
    public void Parses_And_Resolves_Message_By_Topic()
    {
        var dir = WriteProtos(("demo.proto", SampleProto));
        var config = new ProtobufSchemaManifest
        {
            Schemas =
            {
                new ProtobufSchemaMapping
                {
                    Files = { "demo.proto" },
                    TopicPattern = "sensors/+/data",
                    MessageType = "demo.Outer"
                }
            }
        };

        var registry = new ProtobufSchemaRegistry(config, dir);

        registry.HasAnySchemas.Should().BeTrue();
        registry.Diagnostics.Should().NotContain(d => d.Contains("ERROR"));
        registry.TryResolveByTopic("sensors/abc/data", out var msg).Should().BeTrue();
        msg.Name.Should().Be("Outer");
        registry.TryResolveByTopic("sensors/abc/other", out _).Should().BeFalse();
    }

    [Test]
    public void Resolves_Nested_Message_And_Enum_By_FullyQualifiedName()
    {
        var dir = WriteProtos(("demo.proto", SampleProto));
        var config = new ProtobufSchemaManifest
        {
            Schemas =
            {
                new ProtobufSchemaMapping
                {
                    Files = { "demo.proto" }, TopicPattern = "x", MessageType = "demo.Outer"
                }
            }
        };

        var registry = new ProtobufSchemaRegistry(config, dir);

        registry.TryResolveMessage(".demo.Inner", out var inner).Should().BeTrue();
        inner.Name.Should().Be("Inner");
        registry.TryResolveEnum(".demo.Color", out var color).Should().BeTrue();
        color.Name.Should().Be("Color");
    }

    [Test]
    public void Surfaces_Parse_Errors_For_Invalid_Proto()
    {
        var dir = WriteProtos(("bad.proto", "syntax = \"proto3\"; message { oops }"));
        var config = new ProtobufSchemaManifest
        {
            Schemas =
            {
                new ProtobufSchemaMapping { Files = { "bad.proto" }, TopicPattern = "x", MessageType = "demo.Nope" }
            }
        };

        var registry = new ProtobufSchemaRegistry(config, dir);

        registry.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public void Rooted_File_Path_Does_Not_Throw_And_Adds_Diagnostic()
    {
        var dir = WriteProtos(("demo.proto", SampleProto));
        var rootedPath = Path.Combine(dir, "demo.proto");
        var config = new ProtobufSchemaManifest
        {
            Schemas =
            {
                new ProtobufSchemaMapping
                {
                    Files = { rootedPath },
                    TopicPattern = "x",
                    MessageType = "demo.Outer"
                }
            }
        };

        var act = () => new ProtobufSchemaRegistry(config, dir);

        act.Should().NotThrow();
        var registry = act();
        registry.Diagnostics.Should().Contain(d => d.Contains(rootedPath));
    }

    [Test]
    public void Empty_Config_Has_No_Schemas()
    {
        var registry = new ProtobufSchemaRegistry(new ProtobufSchemaManifest(), Path.GetTempPath());
        registry.HasAnySchemas.Should().BeFalse();
        registry.TryResolveByTopic("anything", out _).Should().BeFalse();
    }

    [TestCase("spBv1.0/+/DDATA/+/+")]
    [TestCase("spBv1.0/+/NBIRTH/+")]
    [TestCase("#")]
    [TestCase("+/foo/bar")]
    public void Warns_When_Topic_Pattern_Overlaps_Sparkplug_Namespace(string topicPattern)
    {
        var dir = WriteProtos(("demo.proto", SampleProto));
        var config = new ProtobufSchemaManifest
        {
            Schemas =
            {
                new ProtobufSchemaMapping
                {
                    Files = { "demo.proto" },
                    TopicPattern = topicPattern,
                    MessageType = "demo.Outer"
                }
            }
        };

        var registry = new ProtobufSchemaRegistry(config, dir);

        registry.Diagnostics.Should().Contain(d => d.Contains("overlaps the Sparkplug B"));
    }

    [Test]
    public void Does_Not_Warn_For_NonOverlapping_Topic_Pattern()
    {
        var dir = WriteProtos(("demo.proto", SampleProto));
        var config = new ProtobufSchemaManifest
        {
            Schemas =
            {
                new ProtobufSchemaMapping
                {
                    Files = { "demo.proto" },
                    TopicPattern = "application/+/device/+/event/up",
                    MessageType = "demo.Outer"
                }
            }
        };

        var registry = new ProtobufSchemaRegistry(config, dir);

        registry.Diagnostics.Should().NotContain(d => d.Contains("overlaps the Sparkplug B"));
    }
}
