using System.Text;
using Google.Protobuf;
using MQTTnet;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Plugins.BuiltIn;

// The spec encodes STATE as JSON UTF-8, never protobuf, so these cases pin that the
// protobuf parser never sees a STATE topic. Captured from broker.hivemq.com.
[TestFixture]
public class SparkplugPayloadDecoderTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgsBytes(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    [Test]
    public void State_JsonPayload_SucceedsWithJsonDisplayText()
    {
        var decoder = new SparkplugPayloadDecoder();
        const string json = """{"online":true,"timestamp":1791368292164}""";
        var result = decoder.Decode(MakeArgs("spBv1.0/STATE/IamHost", json));

        result.IsFailure.Should().BeFalse();
        result.FailureReason.Should().BeNull();
        result.DisplayText.Should().Be(json);
        result.FormatId.Should().Be("sparkplug-b");
        result.Topic.Should().Be("spBv1.0/STATE/IamHost");
    }

    [Test]
    public void State_JsonPayload_HasNoTypedProtobufPayload()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/STATE/dados", """{"online":false,"timestamp":1}"""));

        result.TypedPayload.Should().BeNull();
    }

    [Test]
    public void State_OfflineJsonPayload_Succeeds()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/STATE/dados", """{"online":false,"timestamp":1791240770483}"""));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Contain("false");
    }

    [Test]
    public void State_PlainTextPayload_Succeeds()
    {
        // Sparkplug 2.2 encodes STATE as the bare strings ONLINE/OFFLINE.
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/STATE/primary_host", "ONLINE"));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().Be("ONLINE");
    }

    [Test]
    public void State_InvalidUtf8Payload_FailsWithStateReason()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgsBytes("spBv1.0/STATE/primary_host", [0xFF, 0xFE, 0x80]));

        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().NotBeNullOrEmpty();
        result.FailureReason.Should().Contain("UTF-8");
        result.FailureReason.Should().NotContain("protobuf");
    }

    [Test]
    public void State_EmptyPayload_SucceedsWithEmptyDisplayText()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0/STATE/primary_host", ""));

        result.IsFailure.Should().BeFalse();
        result.DisplayText.Should().BeEmpty();
    }

    [Test]
    public void NData_ProtobufPayload_StillSetsTypedPayload()
    {
        var decoder = new SparkplugPayloadDecoder();
        var payload = new Payload { Timestamp = 1234567890, Seq = 7 };
        var result = decoder.Decode(MakeArgsBytes("spBv1.0/group/NDATA/eon1", payload.ToByteArray()));

        result.IsFailure.Should().BeFalse();
        result.TypedPayload.Should().BeOfType<Payload>();
        ((Payload)result.TypedPayload!).Seq.Should().Be(7);
    }

    [Test]
    public void DData_JsonPayload_StillFails()
    {
        // Every verb except STATE must be protobuf, so JSON here stays a failure.
        var decoder = new SparkplugPayloadDecoder();
        const string json = """{"angulo_carga_a":{"value":102.50}}""";
        var result = decoder.Decode(MakeArgs("spBv1.0/group/DDATA/eon1/dev1", json));

        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().Contain("protobuf");
    }

    [Test]
    public void TopicWithStateSegmentElsewhere_UsesProtobuf()
    {
        var decoder = new SparkplugPayloadDecoder();
        var payload = new Payload { Timestamp = 42 };
        var result = decoder.Decode(MakeArgsBytes("spBv1.0/STATE/NDATA/eon1", payload.ToByteArray()));

        result.IsFailure.Should().BeFalse();
        result.TypedPayload.Should().BeOfType<Payload>();
    }

    [Test]
    public void NonSparkplugPrefix_IsNotTreatedAsState()
    {
        var decoder = new SparkplugPayloadDecoder();
        var result = decoder.Decode(MakeArgs("spBv1.0x/STATE/primary_host", "ONLINE"));

        result.IsFailure.Should().BeTrue();
    }

    [Test]
    public void State_JsonPayload_RawPayloadIsPreserved()
    {
        var decoder = new SparkplugPayloadDecoder();
        const string json = """{"online":true,"timestamp":1}""";
        var result = decoder.Decode(MakeArgs("spBv1.0/STATE/IamHost", json));

        result.RawPayload.Should().Equal(Encoding.UTF8.GetBytes(json));
    }
}
