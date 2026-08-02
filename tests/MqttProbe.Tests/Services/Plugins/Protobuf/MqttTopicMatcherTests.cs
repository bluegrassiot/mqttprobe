using MqttProbe.Services.Plugins.Protobuf;

namespace MqttProbe.Tests.Services.Plugins.Protobuf;

[TestFixture]
public class MqttTopicMatcherTests
{
    [TestCase("application/1/device/00aa/event/up", "application/+/device/+/event/up", true)]
    [TestCase("application/1/device/00aa/event/join", "application/+/device/+/event/up", false)]
    [TestCase("application/1/device/00aa/event/up", "application/#", true)]
    [TestCase("a/b/c", "a/b/c", true)]
    [TestCase("a/b", "a/b/c", false)]
    [TestCase("spBv1.0/group/NBIRTH/node", "application/+/device/+/event/up", false)]
    public void Matches_Wildcards(string topic, string filter, bool expected)
    {
        MqttTopicMatcher.Matches(topic, filter).Should().Be(expected);
    }
}
