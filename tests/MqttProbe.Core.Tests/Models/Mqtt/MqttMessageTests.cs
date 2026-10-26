using System.Text.Json;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Tests.Models.Mqtt;

[TestFixture]
public class MqttMessageTests
{
    [Test]
    public void ParameterisedConstructor_SetsAllProperties()
    {
        var before = DateTime.UtcNow;
        var msg = new MqttMessage("payload", "topic/a", true, MqttQualityOfServiceLevel.AtLeastOnce);

        msg.Payload.Should().Be("payload");
        msg.Topic.Should().Be("topic/a");
        msg.RetainedMessage.Should().BeTrue();
        msg.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtLeastOnce);
        msg.DateTimeReceived.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Test]
    public void Id_IsNonEmptyGuid_ForBothConstructors()
    {
        var parameterised = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce);
        var defaultConstructed = new MqttMessage();

        parameterised.Id.Should().NotBe(Guid.Empty);
        defaultConstructed.Id.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Id_IsUniquePerInstance()
    {
        var a = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce);
        var b = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce);

        a.Id.Should().NotBe(b.Id);
    }

    [Test]
    public void DefaultConstructor_Payload_CanBeNullOrEmpty()
    {
        var msg = new MqttMessage { Payload = null };
        msg.Payload.Should().BeNull();

        msg.Payload = string.Empty;
        msg.Payload.Should().BeEmpty();
    }

    [Test]
    public void DefaultConstructor_QualityOfServiceLevel_DefaultsToAtMostOnce()
    {
        var msg = new MqttMessage();
        msg.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtMostOnce);
    }

    [Test]
    public void ParameterisedConstructor_Timestamp_IsSetOnConstruction()
    {
        var before = DateTime.UtcNow;
        var msg = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce);
        var after = DateTime.UtcNow;

        msg.DateTimeReceived.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Test]
    public void DefaultConstructor_Timestamp_IsSetOnConstruction()
    {
        var before = DateTime.UtcNow;
        var msg = new MqttMessage();
        var after = DateTime.UtcNow;

        msg.DateTimeReceived.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Test]
    public void JsonDeserialization_MissingIdAndTimestamp_AssignsGeneratedValues()
    {
        var before = DateTime.UtcNow;

        var msg = JsonSerializer.Deserialize<MqttMessage>(
            """{"Payload":"payload","Topic":"topic/a","RetainedMessage":false,"QualityOfServiceLevel":0}""");

        msg.Should().NotBeNull();
        msg!.Id.Should().NotBe(Guid.Empty);
        msg.DateTimeReceived.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Test]
    public void JsonDeserialization_DefaultTimestamp_UsesConstructionTimestamp()
    {
        var before = DateTime.UtcNow;

        var msg = JsonSerializer.Deserialize<MqttMessage>(
            """{"Payload":"payload","Topic":"topic/a","DateTimeReceived":"0001-01-01T00:00:00"}""");

        msg.Should().NotBeNull();
        msg!.DateTimeReceived.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Test]
    public void FormatId_DefaultsToNull()
    {
        var msg = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce);
        msg.FormatId.Should().BeNull();
    }

    [Test]
    public void FormatId_CanBeSetViaInit()
    {
        var msg = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            FormatId = "json"
        };
        msg.FormatId.Should().Be("json");
    }

    [Test]
    public void V5Properties_DefaultToNull()
    {
        var msg = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce);

        msg.ContentType.Should().BeNull();
        msg.ResponseTopic.Should().BeNull();
        msg.CorrelationData.Should().BeNull();
        msg.MessageExpiryInterval.Should().BeNull();
        msg.PayloadFormatIndicator.Should().BeNull();
        msg.UserProperties.Should().BeNull();
    }

    [Test]
    public void V5Properties_RoundTripThroughJson()
    {
        var original = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            ContentType = "application/json",
            ResponseTopic = "reply/here",
            CorrelationData = [1, 2, 3],
            MessageExpiryInterval = 60,
            PayloadFormatIndicator = MqttPayloadFormatIndicator.CharacterData,
            UserProperties = [new MqttMessageUserProperty("site", "lab"), new MqttMessageUserProperty("schema", "v1")]
        };

        var copy = JsonSerializer.Deserialize<MqttMessage>(JsonSerializer.Serialize(original));

        copy.Should().NotBeNull();
        copy!.ContentType.Should().Be("application/json");
        copy.ResponseTopic.Should().Be("reply/here");
        copy.CorrelationData.Should().Equal(1, 2, 3);
        copy.MessageExpiryInterval.Should().Be(60u);
        copy.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.CharacterData);
        copy.UserProperties.Should().Equal(
            new MqttMessageUserProperty("site", "lab"),
            new MqttMessageUserProperty("schema", "v1"));
    }
}
