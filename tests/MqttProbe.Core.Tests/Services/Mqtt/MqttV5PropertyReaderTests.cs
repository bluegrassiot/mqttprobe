using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttV5PropertyReaderTests
{
    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static MqttApplicationMessage Plain() =>
        new MqttApplicationMessageBuilder().WithTopic("t").WithPayload("p").Build();

    [Test]
    public void PlainMessage_AllPropertiesAreNull()
    {
        var message = Plain();

        MqttV5PropertyReader.ContentType(message).Should().BeNull();
        MqttV5PropertyReader.ResponseTopic(message).Should().BeNull();
        MqttV5PropertyReader.CorrelationData(message).Should().BeNull();
        MqttV5PropertyReader.MessageExpiryInterval(message).Should().BeNull();
        MqttV5PropertyReader.PayloadFormatIndicator(message).Should().BeNull();
        MqttV5PropertyReader.UserProperties(message).Should().BeNull();
    }

    [Test]
    public void EmptyStringsAndZeroValues_AreTreatedAsAbsent()
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithContentType(string.Empty)
            .WithResponseTopic(string.Empty)
            .WithCorrelationData([])
            .WithMessageExpiryInterval(0)
            .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.Unspecified)
            .Build();

        MqttV5PropertyReader.ContentType(message).Should().BeNull();
        MqttV5PropertyReader.ResponseTopic(message).Should().BeNull();
        MqttV5PropertyReader.CorrelationData(message).Should().BeNull();
        MqttV5PropertyReader.MessageExpiryInterval(message).Should().BeNull();
        MqttV5PropertyReader.PayloadFormatIndicator(message).Should().BeNull();
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

        MqttV5PropertyReader.ContentType(message).Should().Be("application/json");
        MqttV5PropertyReader.ResponseTopic(message).Should().Be("reply/here");
        MqttV5PropertyReader.CorrelationData(message).Should().Equal(0xAB, 0xCD);
        MqttV5PropertyReader.MessageExpiryInterval(message).Should().Be(300u);
        MqttV5PropertyReader.PayloadFormatIndicator(message).Should().Be(MqttPayloadFormatIndicator.CharacterData);
        MqttV5PropertyReader.UserProperties(message).Should().Equal(
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

        MqttV5PropertyReader.UserProperties(message).Should().Equal(
            new MqttMessageUserProperty("tag", "first"),
            new MqttMessageUserProperty("tag", "second"));
    }
}
