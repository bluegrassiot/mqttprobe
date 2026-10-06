using System.Text;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Tests.Services.Plugins.Protobuf;

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

    [Test]
    public void Matches_HashPreservesStandardMqttSysSemantics()
    {
        MqttTopicMatcher.Matches("$SYS/broker/uptime", "#").Should().BeFalse();
    }

    [TestCase("a/#/b")]
    [TestCase("a/+suffix")]
    [TestCase("a#")]
    public void IsValidFilter_RejectsMisplacedWildcards(string filter)
    {
        MqttTopicMatcher.IsValidFilter(filter).Should().BeFalse();
    }

    [TestCase("#")]
    [TestCase("a/+")]
    [TestCase("a/b/#")]
    public void IsValidFilter_AllowsValidWildcards(string filter)
    {
        MqttTopicMatcher.IsValidFilter(filter).Should().BeTrue();
    }

    [TestCase(65_535, true)]
    [TestCase(65_536, false)]
    public void IsValidFilter_LimitsAsciiFiltersByByteCount(int charCount, bool expected)
    {
        MqttTopicMatcher.IsValidFilter(new string('a', charCount)).Should().Be(expected);
    }

    [Test]
    public void IsValidFilter_AllowsTwoByteCharFilterAtExactlyUtf8Limit()
    {
        var filter = new string('é', 32_767) + "a";
        Encoding.UTF8.GetByteCount(filter).Should().Be(65_535);

        MqttTopicMatcher.IsValidFilter(filter).Should().BeTrue();
    }

    [Test]
    public void IsValidFilter_RejectsTwoByteCharFilterPastUtf8Limit()
    {
        var filter = new string('é', 32_768);
        Encoding.UTF8.GetByteCount(filter).Should().Be(65_536);
        filter.Length.Should().BeLessThan(65_535);

        MqttTopicMatcher.IsValidFilter(filter).Should().BeFalse();
    }

    [Test]
    public void IsValidFilter_RejectsAstralCharFilterPastUtf8Limit()
    {
        var filter = string.Concat(Enumerable.Repeat("😀", 16_384));
        Encoding.UTF8.GetByteCount(filter).Should().Be(65_536);
        filter.Length.Should().BeLessThan(65_535);

        MqttTopicMatcher.IsValidFilter(filter).Should().BeFalse();
    }

    [Test]
    public void Matches_RejectsFilterPastUtf8Limit()
    {
        var topic = new string('é', 32_768);

        MqttTopicMatcher.Matches(topic, topic).Should().BeFalse();
    }
}
