using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttStringRulesTests
{
    [Test]
    public void IsValidUtf8String_RejectsNullAndNul()
    {
        MqttStringRules.IsValidUtf8String(null).Should().BeFalse();
        MqttStringRules.IsValidUtf8String("trace\0id").Should().BeFalse();
        MqttStringRules.IsValidUtf8String(string.Empty).Should().BeTrue("empty is a valid, if useless, UTF-8 string");
    }

    [Test]
    public void IsValidUtf8String_AsciiBoundary()
    {
        MqttStringRules.IsValidUtf8String(new string('a', MqttStringRules.MaxUtf8Bytes)).Should().BeTrue();
        MqttStringRules.IsValidUtf8String(new string('a', MqttStringRules.MaxUtf8Bytes + 1)).Should().BeFalse();
    }

    [Test]
    public void IsValidUtf8String_CountsBytesNotCharacters()
    {
        var euros = MqttStringRules.MaxUtf8Bytes / 3;

        MqttStringRules.IsValidUtf8String(string.Concat(Enumerable.Repeat("€", euros))).Should().BeTrue("21,845 × 3 bytes = 65,535");
        MqttStringRules.IsValidUtf8String(string.Concat(Enumerable.Repeat("€", euros + 1))).Should().BeFalse("21,846 × 3 bytes = 65,538");
    }
}
