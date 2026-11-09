using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Tests.Models.Mqtt;

[TestFixture]
public class SubscribedTopicTests
{
    [Test]
    public void DefaultQos_WhenOmitted_IsAtMostOnce()
    {
        var topic = new SubscribedTopic { Topic = "a/b" };

        topic.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtMostOnce);
    }

    [TestCase(MqttQualityOfServiceLevel.AtMostOnce)]
    [TestCase(MqttQualityOfServiceLevel.AtLeastOnce)]
    [TestCase(MqttQualityOfServiceLevel.ExactlyOnce)]
    public void ExplicitQos_IsPreserved(MqttQualityOfServiceLevel qos)
    {
        var topic = new SubscribedTopic { Topic = "a/b", QualityOfServiceLevel = qos };

        topic.QualityOfServiceLevel.Should().Be(qos);
    }
}
