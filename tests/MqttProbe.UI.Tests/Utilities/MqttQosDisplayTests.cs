using MQTTnet.Protocol;
using MqttProbe.UI.Utilities;

namespace MqttProbe.UI.Tests.Utilities;

[TestFixture]
public class MqttQosDisplayTests
{
    [TestCase(MqttQualityOfServiceLevel.AtMostOnce, "0 · At most once")]
    [TestCase(MqttQualityOfServiceLevel.AtLeastOnce, "1 · At least once")]
    [TestCase(MqttQualityOfServiceLevel.ExactlyOnce, "2 · Exactly once")]
    public void Format_KnownValues_ReturnsExpectedLabel(MqttQualityOfServiceLevel qos, string expected)
    {
        MqttQosDisplay.Format(qos).Should().Be(expected);
    }

    [Test]
    public void Format_UnknownEnumValue_ReturnsNumericWithRawToken()
    {
        var unknown = (MqttQualityOfServiceLevel)99;

        var result = MqttQosDisplay.Format(unknown);

        result.Should().Be("99 · 99");
    }

    [Test]
    public void Format_AtMostOnce_ContainsMiddleDot()
    {
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.AtMostOnce)
            .Should().Contain(" · ");
    }

    [Test]
    public void Format_AtLeastOnce_ContainsMiddleDot()
    {
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.AtLeastOnce)
            .Should().Contain(" · ");
    }

    [Test]
    public void Format_ExactlyOnce_ContainsMiddleDot()
    {
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.ExactlyOnce)
            .Should().Contain(" · ");
    }

    [Test]
    public void Format_KnownValues_NeverBlank()
    {
        foreach (MqttQualityOfServiceLevel qos in Enum.GetValues<MqttQualityOfServiceLevel>())
        {
            MqttQosDisplay.Format(qos).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void Format_KnownValues_StartsWithNumericValue()
    {
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.AtMostOnce).Should().StartWith("0");
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.AtLeastOnce).Should().StartWith("1");
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.ExactlyOnce).Should().StartWith("2");
    }

    [Test]
    public void Format_KnownValues_LabelsAreSentenceCase()
    {
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.AtMostOnce).Should().Contain("At most once");
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.AtLeastOnce).Should().Contain("At least once");
        MqttQosDisplay.Format(MqttQualityOfServiceLevel.ExactlyOnce).Should().Contain("Exactly once");
    }
}
