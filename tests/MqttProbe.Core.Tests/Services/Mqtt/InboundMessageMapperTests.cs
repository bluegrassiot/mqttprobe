using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class InboundMessageMapperTests
{
    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static MqttApplicationMessage Plain() =>
        new MqttApplicationMessageBuilder().WithTopic("t").WithPayload("p").Build();

    [Test]
    public void PlainMessage_AllPropertiesAreNull()
    {
        var message = Plain();

        InboundMessageMapper.ContentType(message).Should().BeNull();
        InboundMessageMapper.ResponseTopic(message).Should().BeNull();
        InboundMessageMapper.CorrelationData(message).Should().BeNull();
        InboundMessageMapper.MessageExpiryInterval(message).Should().BeNull();
        InboundMessageMapper.PayloadFormatIndicator(message).Should().BeNull();
        InboundMessageMapper.UserProperties(message).Should().BeNull();
    }

    [Test]
    public void PresentButEmptyValues_ArePreservedAsEmpty()
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithContentType(string.Empty)
            .WithResponseTopic(string.Empty)
            .WithCorrelationData([])
            .Build();

        InboundMessageMapper.ContentType(message).Should().BeEmpty();
        InboundMessageMapper.ResponseTopic(message).Should().BeEmpty();
        InboundMessageMapper.CorrelationData(message).Should().NotBeNull().And.BeEmpty();
    }

    [Test]
    public void ZeroExpiryAndUnspecifiedPayloadFormat_AreIndistinguishableFromAbsent_SoMapToNull()
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithMessageExpiryInterval(0)
            .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.Unspecified)
            .Build();

        InboundMessageMapper.MessageExpiryInterval(message).Should().BeNull();
        InboundMessageMapper.PayloadFormatIndicator(message).Should().BeNull();
    }

    [Test]
    public void PopulatedMessage_MapsEveryProperty()
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithContentType("application/json")
            .WithResponseTopic("reply/here")
            .WithCorrelationData([0xAB, 0xCD])
            .WithMessageExpiryInterval(300)
            .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.CharacterData)
            .WithUserProperty("source", Utf8("paho"))
            .WithUserProperty("site", Utf8("lab"))
            .Build();

        InboundMessageMapper.ContentType(message).Should().Be("application/json");
        InboundMessageMapper.ResponseTopic(message).Should().Be("reply/here");
        InboundMessageMapper.CorrelationData(message).Should().Equal(0xAB, 0xCD);
        InboundMessageMapper.MessageExpiryInterval(message).Should().Be(300u);
        InboundMessageMapper.PayloadFormatIndicator(message).Should().Be(MqttPayloadFormatIndicator.CharacterData);
        InboundMessageMapper.UserProperties(message).Should().Equal(
            new MqttMessageUserProperty("source", "paho"),
            new MqttMessageUserProperty("site", "lab"));
    }

    [Test]
    public void DuplicateUserPropertyNames_PreserveOrderAndBothValues()
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithUserProperty("tag", Utf8("first"))
            .WithUserProperty("tag", Utf8("second"))
            .Build();

        InboundMessageMapper.UserProperties(message).Should().Equal(
            new MqttMessageUserProperty("tag", "first"),
            new MqttMessageUserProperty("tag", "second"));
    }

    [Test]
    public void Map_CopiesPayloadTopicFlagsAndEnrichment()
    {
        var source = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithPayload("raw")
            .WithRetainFlag()
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
            .WithContentType("text/plain")
            .WithUserProperty("k", Utf8("v"))
            .Build();
        var aliases = new Dictionary<ulong, string> { [1] = "Temp" };

        var mapped = InboundMessageMapper.Map(source, "decoded", "t", aliases, "json");

        mapped.Payload.Should().Be("decoded");
        mapped.Topic.Should().Be("t");
        mapped.RetainedMessage.Should().BeTrue();
        mapped.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
        mapped.AliasNames.Should().BeEquivalentTo(aliases);
        mapped.FormatId.Should().Be("json");
        mapped.ContentType.Should().Be("text/plain");
        mapped.UserProperties.Should().Equal(new MqttMessageUserProperty("k", "v"));
    }
}
