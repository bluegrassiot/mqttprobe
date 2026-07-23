using Google.Protobuf;
using MqttProbe.Models.Plugins;
using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class ProtobufWireDecoderTests
{
    private const string Proto = """
        syntax = "proto3";
        package demo;
        enum Color { UNKNOWN = 0; RED = 1; GREEN = 2; }
        message Inner { string label = 1; }
        message Outer {
          int32 id = 1;
          Inner inner = 2;
          Color color = 3;
          repeated int32 values = 4;
          string name = 5;
          bool active = 6;
          double ratio = 7;
        }
        message Drift {
          sint32 s32 = 1;
          int32 dup = 13;
          int32 neg = 15;
        }
        message Node {
          Node child = 1;
          int32 v = 2;
        }
        """;

    private static ProtobufSchemaRegistry BuildRegistry(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "mqttprobe-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "demo.proto"), Proto);
        var config = new ProtobufSchemaManifest
        {
            Schemas = { new ProtobufSchemaMapping { Files = { "demo.proto" }, TopicPattern = "x", MessageType = "demo.Outer" } }
        };
        return new ProtobufSchemaRegistry(config, dir);
    }

    private static byte[] EncodeOuter()
    {
        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);

        cos.WriteTag(1, WireFormat.WireType.Varint);
        cos.WriteInt32(42);

        var innerBytes = EncodeInnerLabel("hi");
        cos.WriteTag(2, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(innerBytes));

        cos.WriteTag(3, WireFormat.WireType.Varint);
        cos.WriteEnum(2);

        cos.WriteTag(4, WireFormat.WireType.LengthDelimited);
        cos.WriteLength(3);
        cos.WriteRawTag(1); cos.WriteRawTag(2); cos.WriteRawTag(3);

        cos.WriteTag(5, WireFormat.WireType.LengthDelimited);
        cos.WriteString("dev");

        cos.WriteTag(6, WireFormat.WireType.Varint);
        cos.WriteBool(true);

        cos.WriteTag(7, WireFormat.WireType.Fixed64);
        cos.WriteDouble(0.5);

        cos.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodeInnerLabel(string label)
    {
        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);
        cos.WriteTag(1, WireFormat.WireType.LengthDelimited);
        cos.WriteString(label);
        cos.Flush();
        return ms.ToArray();
    }

    [Test]
    public void Decodes_All_Scalar_And_Nested_Fields()
    {
        var registry = BuildRegistry(out _);
        registry.TryResolveMessage("demo.Outer", out var outer).Should().BeTrue();

        var decoder = new ProtobufWireDecoder(registry);
        var result = decoder.Decode(EncodeOuter(), outer);

        result["id"].Should().Be(42L);
        ((Dictionary<string, object?>)result["inner"]!)["label"].Should().Be("hi");
        result["color"].Should().Be("GREEN");
        ((System.Collections.IEnumerable)result["values"]!).Cast<object?>()
            .Select(Convert.ToInt64).Should().Equal(1L, 2L, 3L);
        result["name"].Should().Be("dev");
        result["active"].Should().Be(true);
        result["ratio"].Should().Be(0.5d);
    }

    [Test]
    public void Unknown_Field_Is_Preserved()
    {
        var registry = BuildRegistry(out _);
        registry.TryResolveMessage("demo.Inner", out var inner).Should().BeTrue();

        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);
        cos.WriteTag(99, WireFormat.WireType.Varint);
        cos.WriteInt32(7);
        cos.Flush();

        var decoder = new ProtobufWireDecoder(registry);
        var result = decoder.Decode(ms.ToArray(), inner);

        result.Keys.Should().Contain("field_99");
    }

    [Test]
    public void WireType_Mismatch_Does_Not_Corrupt_Following_Fields()
    {
        var registry = BuildRegistry(out _);
        registry.TryResolveMessage("demo.Drift", out var drift).Should().BeTrue();

        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);

        // Field 15 is declared int32 (varint) but arrives length-delimited,
        // carrying a 3-byte body that itself looks like a valid varint field.
        cos.WriteTag(15, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom([0x08, 0x96, 0x01]));

        // The next field must still decode correctly.
        cos.WriteTag(13, WireFormat.WireType.Varint);
        cos.WriteInt32(1234);
        cos.Flush();

        var decoder = new ProtobufWireDecoder(registry);
        var result = decoder.Decode(ms.ToArray(), drift);

        result["dup"].Should().Be(1234L);
        result.Keys.Should().Contain("field_15");
        result.Keys.Should().NotContain("neg");

        result.Keys.Should().NotContain("s32");
    }

    [Test]
    public void Excessive_Nesting_Throws_InvalidDataException()
    {
        var registry = BuildRegistry(out _);
        registry.TryResolveMessage("demo.Node", out var node).Should().BeTrue();

        using var leaf = new MemoryStream();
        var leafOut = new CodedOutputStream(leaf);
        leafOut.WriteTag(2, WireFormat.WireType.Varint);
        leafOut.WriteInt32(7);
        leafOut.Flush();

        var payload = leaf.ToArray();
        for (var i = 0; i < 300; i++)
        {
            using var wrap = new MemoryStream();
            var wrapOut = new CodedOutputStream(wrap);
            wrapOut.WriteTag(1, WireFormat.WireType.LengthDelimited);
            wrapOut.WriteBytes(ByteString.CopyFrom(payload));
            wrapOut.Flush();
            payload = wrap.ToArray();
        }

        var decoder = new ProtobufWireDecoder(registry);
        var act = () => decoder.Decode(payload, node);

        act.Should().Throw<InvalidDataException>().WithMessage("*nesting*");
    }

    [Test]
    public void Duplicate_Singular_Scalar_Keeps_Last_Value_As_Scalar()
    {
        var registry = BuildRegistry(out _);
        registry.TryResolveMessage("demo.Drift", out var drift).Should().BeTrue();

        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);
        cos.WriteTag(13, WireFormat.WireType.Varint);
        cos.WriteInt32(5);
        cos.WriteTag(13, WireFormat.WireType.Varint);
        cos.WriteInt32(6);
        cos.Flush();

        var decoder = new ProtobufWireDecoder(registry);
        var result = decoder.Decode(ms.ToArray(), drift);

        result["dup"].Should().Be(6L);
        result["dup"].Should().NotBeAssignableTo<System.Collections.IEnumerable>();
    }

    [Test]
    public void Repeated_Unknown_Field_Preserves_All_Occurrences()
    {
        var registry = BuildRegistry(out _);
        registry.TryResolveMessage("demo.Inner", out var inner).Should().BeTrue();

        var tags = new[] { "tag1", "tag2", "tag3" };

        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);
        foreach (var tag in tags)
        {
            cos.WriteTag(99, WireFormat.WireType.LengthDelimited);
            cos.WriteString(tag);
        }
        cos.Flush();

        var decoder = new ProtobufWireDecoder(registry);
        var result = decoder.Decode(ms.ToArray(), inner);

        var expected = tags.Select(t => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(t)));

        ((System.Collections.IEnumerable)result["field_99"]!).Cast<object?>()
            .Should().Equal(expected.Cast<object?>());
    }

    [Test]
    public void Oversized_Length_Prefix_Throws_InvalidDataException()
    {
        var registry = BuildRegistry(out _);
        registry.TryResolveMessage("demo.Drift", out var drift).Should().BeTrue();

        byte[] payload = [0x72, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F];

        var decoder = new ProtobufWireDecoder(registry);
        var act = () => decoder.Decode(payload, drift);

        act.Should().Throw<InvalidDataException>().WithMessage("*Truncated length-delimited*");
    }
}
