using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.TestInfrastructure;

namespace MqttProbe.Core.Tests.Services.Mqtt;

public partial class MessageStoreManagerMessageHandlerTests
{
    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static MqttApplicationMessageReceivedEventArgs MakeV5Args(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithContentType("application/json")
            .WithResponseTopic("reply/here")
            .WithCorrelationData([1, 2, 3])
            .WithMessageExpiryInterval(120)
            .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.CharacterData)
            .WithUserProperty("source", Utf8("paho"))
            .WithUserProperty("site", Utf8("lab"))
            .Build();
        var publishPacket = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, publishPacket, null);
    }

    [Test]
    public async Task MessageReceived_V5Properties_AreStoredOnMessage()
    {
        await _capturedHandler!(MakeV5Args("v5/props", """{"a":1}"""));

        var stored = (await MessageStoreReads.ReadMessagesAsync(_manager, "v5/props", 10))
            .Should().ContainSingle().Subject;

        stored.ContentType.Should().Be("application/json");
        stored.ResponseTopic.Should().Be("reply/here");
        stored.CorrelationData.Should().Equal(1, 2, 3);
        stored.MessageExpiryInterval.Should().Be(120u);
        stored.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.CharacterData);
        stored.UserProperties.Should().Equal(
            new MqttMessageUserProperty("source", "paho"),
            new MqttMessageUserProperty("site", "lab"));
    }

    [Test]
    public async Task MessageReceived_WithoutV5Properties_LeavesPropertiesNull()
    {
        await Fire("v3/plain", "hello");

        var stored = (await MessageStoreReads.ReadMessagesAsync(_manager, "v3/plain", 10))
            .Should().ContainSingle().Subject;

        stored.ContentType.Should().BeNull();
        stored.ResponseTopic.Should().BeNull();
        stored.CorrelationData.Should().BeNull();
        stored.MessageExpiryInterval.Should().BeNull();
        stored.PayloadFormatIndicator.Should().BeNull();
        stored.UserProperties.Should().BeNull();
    }
}
