using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Utilities;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class PublishPropertiesDraftTests
{
    [Test]
    public void NewDraft_HasNothingSet()
    {
        var draft = new PublishPropertiesDraft();

        draft.SetCount.Should().Be(0);
        draft.Any.Should().BeFalse();
        draft.ValidationError.Should().BeNull();
        draft.CanAddUserProperty.Should().BeTrue();
    }

    [Test]
    public void SetCount_CountsScalarsAndEachUserProperty()
    {
        var draft = new PublishPropertiesDraft
        {
            ContentType = "text/plain",
            PayloadFormat = MqttPayloadFormatIndicator.CharacterData,
            ExpirySeconds = 10,
            ResponseTopic = "r",
            CorrelationText = "c"
        };
        draft.UserProperties.Add(new MqttMessageUserProperty("a", "1"));
        draft.UserProperties.Add(new MqttMessageUserProperty("a", "2"));

        draft.SetCount.Should().Be(7);
    }

    [Test]
    public void SetCount_IgnoresEmptyStringsZeroExpiryAndUnspecifiedFormat()
    {
        var draft = new PublishPropertiesDraft
        {
            ContentType = "",
            PayloadFormat = MqttPayloadFormatIndicator.Unspecified,
            ExpirySeconds = 0,
            ResponseTopic = "",
            CorrelationText = ""
        };

        draft.SetCount.Should().Be(0);
    }

    [Test]
    public void ResponseTopicError_FlagsWildcards()
    {
        new PublishPropertiesDraft { ResponseTopic = "a/+/b" }.ResponseTopicError.Should().NotBeNull();
        new PublishPropertiesDraft { ResponseTopic = "a/b" }.ResponseTopicError.Should().BeNull();
        new PublishPropertiesDraft { ResponseTopic = "" }.ResponseTopicError.Should().BeNull();
    }

    [Test]
    public void CorrelationError_FlagsBadHexOnly()
    {
        new PublishPropertiesDraft { CorrelationText = "xyz", CorrelationEncoding = CorrelationDataEncoding.Hex }
            .CorrelationError.Should().NotBeNull();
        new PublishPropertiesDraft { CorrelationText = "xyz", CorrelationEncoding = CorrelationDataEncoding.Text }
            .CorrelationError.Should().BeNull();
    }

    [Test]
    public void ToMessage_CopiesBaseFieldsAndSetProperties()
    {
        var source = new MqttMessage("payload", "t/1", true, MqttQualityOfServiceLevel.ExactlyOnce);
        var draft = new PublishPropertiesDraft
        {
            ContentType = "application/json",
            PayloadFormat = MqttPayloadFormatIndicator.CharacterData,
            ExpirySeconds = 60,
            ResponseTopic = "t/1/reply",
            CorrelationText = "abcd",
            CorrelationEncoding = CorrelationDataEncoding.Hex
        };
        draft.UserProperties.Add(new MqttMessageUserProperty("k", " v "));

        var message = draft.ToMessage(source);

        message.Payload.Should().Be("payload");
        message.Topic.Should().Be("t/1");
        message.RetainedMessage.Should().BeTrue();
        message.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
        message.ContentType.Should().Be("application/json");
        message.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.CharacterData);
        message.MessageExpiryInterval.Should().Be(60u);
        message.ResponseTopic.Should().Be("t/1/reply");
        message.CorrelationData.Should().Equal(0xAB, 0xCD);
        message.UserProperties.Should().Equal(new MqttMessageUserProperty("k", " v "));
    }

    [Test]
    public void ToMessage_EmptyDraft_LeavesEveryPropertyNull()
    {
        var message = new PublishPropertiesDraft().ToMessage(new MqttMessage { Topic = "t" });

        message.ContentType.Should().BeNull();
        message.PayloadFormatIndicator.Should().BeNull();
        message.MessageExpiryInterval.Should().BeNull();
        message.ResponseTopic.Should().BeNull();
        message.CorrelationData.Should().BeNull();
        message.UserProperties.Should().BeNull();
        message.Payload.Should().BeEmpty();
    }

    [Test]
    public void From_PopulatesEveryFieldAndPicksHexForBinaryCorrelation()
    {
        var message = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            ContentType = "text/plain",
            PayloadFormatIndicator = MqttPayloadFormatIndicator.CharacterData,
            MessageExpiryInterval = 42,
            ResponseTopic = "reply",
            CorrelationData = [0x00, 0xFF],
            UserProperties = [new MqttMessageUserProperty("a", "1"), new MqttMessageUserProperty("b", "2")]
        };

        var draft = PublishPropertiesDraft.From(message);

        draft.ContentType.Should().Be("text/plain");
        draft.PayloadFormat.Should().Be(MqttPayloadFormatIndicator.CharacterData);
        draft.ExpirySeconds.Should().Be(42u);
        draft.ResponseTopic.Should().Be("reply");
        draft.CorrelationEncoding.Should().Be(CorrelationDataEncoding.Hex);
        draft.CorrelationText.Should().Be("00ff");
        draft.UserProperties.Should().Equal(message.UserProperties!);
        draft.Any.Should().BeTrue();
    }

    [Test]
    public void From_PlainMessage_IsEmptyDraft()
    {
        var draft = PublishPropertiesDraft.From(new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce));

        draft.Any.Should().BeFalse();
        draft.CorrelationEncoding.Should().Be(CorrelationDataEncoding.Text);
    }

    [Test]
    public void From_ThenToMessage_RoundTripsTextCorrelation()
    {
        var original = new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            CorrelationData = "order  42"u8.ToArray()
        };

        var roundTripped = PublishPropertiesDraft.From(original).ToMessage(original);

        roundTripped.CorrelationData.Should().Equal(original.CorrelationData);
    }

    [Test]
    public void Clear_ResetsEverything()
    {
        var draft = new PublishPropertiesDraft { ContentType = "x", ExpirySeconds = 5, CorrelationText = "ab", CorrelationEncoding = CorrelationDataEncoding.Hex };
        draft.UserProperties.Add(new MqttMessageUserProperty("k", "v"));

        draft.Clear();

        draft.Any.Should().BeFalse();
        draft.UserProperties.Should().BeEmpty();
        draft.CorrelationEncoding.Should().Be(CorrelationDataEncoding.Text);
    }

    [Test]
    public void CanAddUserProperty_FalseAtLimit()
    {
        var draft = new PublishPropertiesDraft();
        for (var i = 0; i < PublishPropertiesDraft.MaxUserProperties; i++)
            draft.UserProperties.Add(new MqttMessageUserProperty($"k{i}", "v"));

        draft.CanAddUserProperty.Should().BeFalse();
    }


    [Test]
    public void UserPropertyError_FlagsOverCapAndClearsOnceRemoved()
    {
        var draft = new PublishPropertiesDraft();
        for (var i = 0; i <= PublishPropertiesDraft.MaxUserProperties; i++)
            draft.UserProperties.Add(new MqttMessageUserProperty($"k{i}", "v"));

        draft.UserPropertyError.Should().Contain("At most 50").And.Contain("Remove 1");
        draft.ValidationError.Should().Be(draft.UserPropertyError);

        draft.UserProperties.RemoveAt(0);

        draft.ValidationError.Should().BeNull();
    }

    [Test]
    public void UserPropertyError_FlagsBlankNameByPosition()
    {
        var draft = new PublishPropertiesDraft();
        draft.UserProperties.Add(new MqttMessageUserProperty("ok", "v"));
        draft.UserProperties.Add(new MqttMessageUserProperty("   ", "v"));

        draft.UserPropertyError.Should().Be("User property 2 has a blank name. Remove it.");
    }

    [Test]
    public void From_KeepsEveryImportedPropertyAndReportsTheOverflow()
    {
        var properties = Enumerable.Range(0, PublishPropertiesDraft.MaxUserProperties + 1)
            .Select(i => new MqttMessageUserProperty($"k{i}", "v")).ToArray();
        var message = new MqttMessage("{}", "t", false, MqttQualityOfServiceLevel.AtMostOnce) { UserProperties = properties };

        var draft = PublishPropertiesDraft.From(message);

        draft.UserProperties.Should().HaveCount(PublishPropertiesDraft.MaxUserProperties + 1, "nothing is dropped silently");
        draft.ValidationError.Should().Contain("Remove 1");
    }


    [Test]
    public void ContentTypeError_FlagsNulAndOverLength()
    {
        new PublishPropertiesDraft { ContentType = "text/\0plain" }.ContentTypeError.Should().Contain("NUL");
        new PublishPropertiesDraft { ContentType = new string('a', MqttStringRules.MaxUtf8Bytes + 1) }.ContentTypeError.Should().Contain("65,535");
        new PublishPropertiesDraft { ContentType = new string('a', MqttStringRules.MaxUtf8Bytes) }.ContentTypeError.Should().BeNull();
        new PublishPropertiesDraft { ContentType = "text/\0plain" }.ValidationError.Should().Contain("Content type");
    }

    [Test]
    public void UserPropertyError_FlagsNulAndOverLengthByPositionAndField()
    {
        var draft = new PublishPropertiesDraft();
        draft.UserProperties.Add(new MqttMessageUserProperty("ok", string.Concat(Enumerable.Repeat("€", MqttStringRules.MaxUtf8Bytes / 3))));
        draft.UserProperties.Add(new MqttMessageUserProperty("trace\0id", "v"));
        draft.UserProperties.Add(new MqttMessageUserProperty("k", new string('v', MqttStringRules.MaxUtf8Bytes + 1)));

        draft.UserPropertyError.Should().Be("User property 2 name cannot contain NUL or exceed 65,535 bytes of UTF-8. Remove it.");

        draft.UserProperties.RemoveAt(1);

        draft.UserPropertyError.Should().Be("User property 2 value cannot contain NUL or exceed 65,535 bytes of UTF-8. Remove it.");

        draft.UserProperties.RemoveAt(1);

        draft.UserPropertyError.Should().BeNull("a multibyte value of exactly 65,535 bytes is allowed");
    }

    [Test]
    public void From_KeepsInvalidImportedValueAndReportsIt()
    {
        var message = new MqttMessage("{}", "t", false, MqttQualityOfServiceLevel.AtMostOnce)
        {
            UserProperties = [new MqttMessageUserProperty("k", "a\0b")]
        };

        var draft = PublishPropertiesDraft.From(message);

        draft.UserProperties.Should().Equal(message.UserProperties!);
        draft.ValidationError.Should().Contain("User property 1 value");
    }

    [Test]
    public void Expiry_ZeroOrEmpty_IsNotSentAndNotAnError()
    {
        foreach (var text in new[] { null, "", "  ", "0", " 0 " })
        {
            var draft = new PublishPropertiesDraft { ExpiryText = text };
            draft.ExpirySeconds.Should().BeNull(text);
            draft.ExpiryError.Should().BeNull(text);
            draft.SetCount.Should().Be(0, text);
        }
    }

    [TestCase("abc")]
    [TestCase("-5")]
    [TestCase("1.5")]
    [TestCase("4294967296")]
    [TestCase("1e3")]
    public void Expiry_InvalidText_IsAnErrorAndNeverSent(string text)
    {
        var draft = new PublishPropertiesDraft { ExpiryText = text };

        draft.ExpiryError.Should().Be(PublishPropertiesDraft.ExpiryErrorText);
        draft.ValidationError.Should().Be(PublishPropertiesDraft.ExpiryErrorText);
        draft.ExpirySeconds.Should().BeNull();
        draft.ToMessage(new MqttMessage("p", "t", false, MqttQualityOfServiceLevel.AtMostOnce)).MessageExpiryInterval.Should().BeNull();
    }

    [Test]
    public void Expiry_MaxValueAccepted_AndSetterRoundTrips()
    {
        new PublishPropertiesDraft { ExpiryText = "4294967295" }.ExpirySeconds.Should().Be(uint.MaxValue);

        var draft = new PublishPropertiesDraft { ExpirySeconds = 42 };
        draft.ExpiryText.Should().Be("42");
        draft.ExpiryError.Should().BeNull();

        draft.Clear();
        draft.ExpiryText.Should().BeNull();
    }
}
