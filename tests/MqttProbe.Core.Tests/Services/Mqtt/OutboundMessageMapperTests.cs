using System.Buffers;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class OutboundMessageMapperTests
{
    private static MqttMessage Populated() =>
        new("{\"a\":1}", "out/topic", true, MqttQualityOfServiceLevel.ExactlyOnce)
        {
            ContentType = "application/json",
            ResponseTopic = "out/topic/reply",
            CorrelationData = [0xAB, 0xCD],
            MessageExpiryInterval = 300,
            PayloadFormatIndicator = MqttPayloadFormatIndicator.CharacterData,
            UserProperties = [new MqttMessageUserProperty("source", "probe"), new MqttMessageUserProperty("site", "lab")]
        };

    [Test]
    public void Build_PlainMessage_CarriesOnlyTopicPayloadQosAndRetain()
    {
        var built = OutboundMessageMapper.Build(new MqttMessage("hello", "a/b", false, MqttQualityOfServiceLevel.AtLeastOnce));

        built.Topic.Should().Be("a/b");
        System.Text.Encoding.UTF8.GetString(built.Payload.ToArray()).Should().Be("hello");
        built.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtLeastOnce);
        built.Retain.Should().BeFalse();
        built.ContentType.Should().BeNull();
        built.ResponseTopic.Should().BeNull();
        built.CorrelationData.Should().BeNull();
        built.MessageExpiryInterval.Should().Be(0u);
        built.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.Unspecified);
        built.UserProperties.Should().BeNullOrEmpty();
    }

    [Test]
    public void Build_PopulatedMessage_SetsEveryV5Property()
    {
        var built = OutboundMessageMapper.Build(Populated());

        built.Topic.Should().Be("out/topic");
        built.Retain.Should().BeTrue();
        built.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
        built.ContentType.Should().Be("application/json");
        built.ResponseTopic.Should().Be("out/topic/reply");
        built.CorrelationData.Should().Equal(0xAB, 0xCD);
        built.MessageExpiryInterval.Should().Be(300u);
        built.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.CharacterData);
        built.UserProperties.Select(p => (p.Name, p.ReadValueAsString()))
            .Should().Equal(("source", "probe"), ("site", "lab"));
    }

    [Test]
    public void Build_EmptyAndZeroValues_AreNotSentBecauseTheEncoderOmitsThem()
    {
        var built = OutboundMessageMapper.Build(new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            ContentType = string.Empty,
            ResponseTopic = string.Empty,
            CorrelationData = [],
            MessageExpiryInterval = 0,
            PayloadFormatIndicator = MqttPayloadFormatIndicator.Unspecified,
            UserProperties = []
        });

        built.ContentType.Should().BeNull();
        built.ResponseTopic.Should().BeNull();
        built.CorrelationData.Should().BeNull();
        built.MessageExpiryInterval.Should().Be(0u);
        built.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.Unspecified);
        built.UserProperties.Should().BeNullOrEmpty();
    }

    [Test]
    public void Build_DuplicateUserPropertyNames_PreserveOrderAndValues()
    {
        var built = OutboundMessageMapper.Build(new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            UserProperties =
            [
                new MqttMessageUserProperty("trace", "1"),
                new MqttMessageUserProperty("trace", "2"),
                new MqttMessageUserProperty("region", "eu")
            ]
        });

        built.UserProperties.Select(p => (p.Name, p.ReadValueAsString()))
            .Should().Equal(("trace", "1"), ("trace", "2"), ("region", "eu"));
    }

    [Test]
    public void Build_UserPropertyValuesKeepWhitespaceAndEmptyValues()
    {
        var built = OutboundMessageMapper.Build(new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            UserProperties = [new MqttMessageUserProperty("k", "  two  spaces "), new MqttMessageUserProperty("empty", "")]
        });

        built.UserProperties.Select(p => p.ReadValueAsString()).Should().Equal("  two  spaces ", "");
    }

    [Test]
    public void Build_ThenMap_RoundTripsEveryProperty()
    {
        var original = Populated();

        var mapped = InboundMessageMapper.Map(OutboundMessageMapper.Build(original), original.Payload!, original.Topic!, null, null);

        mapped.ContentType.Should().Be(original.ContentType);
        mapped.ResponseTopic.Should().Be(original.ResponseTopic);
        mapped.CorrelationData.Should().Equal(original.CorrelationData);
        mapped.MessageExpiryInterval.Should().Be(original.MessageExpiryInterval);
        mapped.PayloadFormatIndicator.Should().Be(original.PayloadFormatIndicator);
        mapped.UserProperties.Should().Equal(original.UserProperties);
        mapped.RetainedMessage.Should().Be(original.RetainedMessage);
        mapped.QualityOfServiceLevel.Should().Be(original.QualityOfServiceLevel);
    }

    [Test]
    public void HasV5Properties_FalseForPlainAndEmptyValues()
    {
        OutboundMessageMapper.HasV5Properties(new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce))
            .Should().BeFalse();
        OutboundMessageMapper.HasV5Properties(new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            ContentType = "",
            ResponseTopic = "",
            CorrelationData = [],
            MessageExpiryInterval = 0,
            PayloadFormatIndicator = MqttPayloadFormatIndicator.Unspecified,
            UserProperties = []
        }).Should().BeFalse();
    }

    [TestCase("content")]
    [TestCase("response")]
    [TestCase("correlation")]
    [TestCase("expiry")]
    [TestCase("format")]
    [TestCase("user")]
    public void HasV5Properties_TrueWhenAnySinglePropertyIsSet(string which)
    {
        var message = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            ContentType = which == "content" ? "text/plain" : null,
            ResponseTopic = which == "response" ? "r" : null,
            CorrelationData = which == "correlation" ? [1] : null,
            MessageExpiryInterval = which == "expiry" ? 5u : null,
            PayloadFormatIndicator = which == "format" ? MqttPayloadFormatIndicator.CharacterData : null,
            UserProperties = which == "user" ? [new MqttMessageUserProperty("k", "v")] : null
        };

        OutboundMessageMapper.HasV5Properties(message).Should().BeTrue();
    }
}
