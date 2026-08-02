using System.Text;
using MqttProbe.Services.Plugins.Contracts;

namespace MqttProbe.Tests.Services.Plugins.Contracts;

[TestFixture]
public class DecodedPayloadEnvelopeTests
{
    // --- CreateSuccess ---

    [Test]
    public void CreateSuccess_SetsAllFields()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello");

        var result = DecodedPayloadEnvelope.CreateSuccess(
            "json", "sensor/temp", payload, """{"temp":21}""");

        result.IsFailure.Should().BeFalse();
        result.FormatId.Should().Be("json");
        result.Topic.Should().Be("sensor/temp");
        result.RawPayload.Should().BeSameAs(payload);
        result.DisplayText.Should().Be("""{"temp":21}""");
        result.TypedPayload.Should().BeNull();
        result.Metadata.Should().BeNull();
        result.FailureReason.Should().BeNull();
    }

    [Test]
    public void CreateSuccess_WithTypedPayload_PreservesObject()
    {
        var typed = new Dictionary<string, int> { ["x"] = 42 };

        var result = DecodedPayloadEnvelope.CreateSuccess(
            "json", "t", [], "display", typedPayload: typed);

        result.TypedPayload.Should().BeSameAs(typed);
        result.TypedPayload.Should().BeOfType<Dictionary<string, int>>()
            .Which["x"].Should().Be(42);
    }

    [Test]
    public void CreateSuccess_WithMetadata_PreservesDictionary()
    {
        var metadata = new Dictionary<string, string>
        {
            ["encoding"] = "utf-8",
            ["schema"] = "v2"
        };

        var result = DecodedPayloadEnvelope.CreateSuccess(
            "json", "t", [], "d", metadata: metadata);

        result.Metadata.Should().NotBeNull();
        result.Metadata!["encoding"].Should().Be("utf-8");
        result.Metadata["schema"].Should().Be("v2");
    }

    [Test]
    public void CreateSuccess_WithTypedPayloadAndMetadata_SetsBoth()
    {
        var typed = "payload-object";
        var metadata = new Dictionary<string, string> { ["k"] = "v" };

        var result = DecodedPayloadEnvelope.CreateSuccess(
            "xml", "topic/a", [1, 2, 3], "display", typed, metadata);

        result.IsFailure.Should().BeFalse();
        result.TypedPayload.Should().BeSameAs(typed);
        result.Metadata!["k"].Should().Be("v");
    }

    [Test]
    public void CreateSuccess_EmptyRawPayload_PreservesEmptyArray()
    {
        var result = DecodedPayloadEnvelope.CreateSuccess(
            "empty", "t", [], "");

        result.RawPayload.Should().BeEmpty();
        result.IsFailure.Should().BeFalse();
    }

    // --- CreateFailure ---

    [Test]
    public void CreateFailure_SetsIsFailureAndReason()
    {
        byte[] payload = [0x01, 0x02];

        var result = DecodedPayloadEnvelope.CreateFailure(
            "sparkplug-b", "spBv1.0/g/NDATA/n", payload, "parse failed");

        result.IsFailure.Should().BeTrue();
        result.FailureReason.Should().Be("parse failed");
        result.FormatId.Should().Be("sparkplug-b");
        result.Topic.Should().Be("spBv1.0/g/NDATA/n");
        result.RawPayload.Should().BeSameAs(payload);
    }

    [Test]
    public void CreateFailure_DisplayTextContainsFailurePrefix()
    {
        var result = DecodedPayloadEnvelope.CreateFailure(
            "json", "t", [], "invalid syntax");

        result.DisplayText.Should().Contain("Decode failed:");
        result.DisplayText.Should().Contain("invalid syntax");
    }

    [Test]
    public void CreateFailure_TypedPayloadIsNull()
    {
        var result = DecodedPayloadEnvelope.CreateFailure(
            "json", "t", [], "bad");

        result.TypedPayload.Should().BeNull();
    }

    [Test]
    public void CreateFailure_MetadataIsNull()
    {
        var result = DecodedPayloadEnvelope.CreateFailure(
            "json", "t", [], "bad");

        result.Metadata.Should().BeNull();
    }

    [Test]
    public void CreateFailure_EmptyRawPayload_PreservesEmptyArray()
    {
        var result = DecodedPayloadEnvelope.CreateFailure(
            "binary", "t", [], "empty decode");

        result.RawPayload.Should().BeEmpty();
        result.IsFailure.Should().BeTrue();
    }
}
