using MQTTnet.Protocol;
using MqttProbe.UI.Utilities;

namespace MqttProbe.UI.Tests.Utilities;

[TestFixture]
public class MqttMessagePropertyDisplayTests
{
    [TestCase(MqttPayloadFormatIndicator.CharacterData, "UTF-8 text")]
    [TestCase(MqttPayloadFormatIndicator.Unspecified, "Unspecified")]
    public void FormatPayloadFormat_ReturnsReadableLabel(MqttPayloadFormatIndicator indicator, string expected) =>
        MqttMessagePropertyDisplay.FormatPayloadFormat(indicator).Should().Be(expected);

    [TestCase(0u, "0 s")]
    [TestCase(45u, "45 s")]
    [TestCase(60u, "60 s (1m)")]
    [TestCase(90u, "90 s (1m 30s)")]
    [TestCase(3600u, "3600 s (1h)")]
    [TestCase(90061u, "90061 s (1d 1h 1m 1s)")]
    public void FormatExpiry_AppendsHumanReadableBreakdownFromOneMinute(uint seconds, string expected) =>
        MqttMessagePropertyDisplay.FormatExpiry(seconds).Should().Be(expected);

    [Test]
    public void FormatCorrelationData_PrintableUtf8_ReturnsText() =>
        MqttMessagePropertyDisplay.FormatCorrelationData("request-7"u8.ToArray()).Should().Be("request-7");

    [Test]
    public void FormatCorrelationData_BinaryBytes_ReturnsHexWithLength() =>
        MqttMessagePropertyDisplay.FormatCorrelationData([0x00, 0xFF, 0x10]).Should().Be("0x00ff10 (3 bytes)");

    [Test]
    public void FormatCorrelationData_InvalidUtf8_ReturnsHex() =>
        MqttMessagePropertyDisplay.FormatCorrelationData([0xC3, 0x28]).Should().Be("0xc328 (2 bytes)");

    [Test]
    public void FormatCorrelationData_EmbeddedTab_ReturnsHexSoItCannotBeMistakenForSpaces() =>
        MqttMessagePropertyDisplay.FormatCorrelationData("a\tb"u8.ToArray()).Should().Be("0x610962 (3 bytes)");

    [Test]
    public void FormatCorrelationData_EmbeddedNewline_ReturnsHex() =>
        MqttMessagePropertyDisplay.FormatCorrelationData("a\nb"u8.ToArray()).Should().Be("0x610a62 (3 bytes)");

    [Test]
    public void FormatCorrelationData_WhitespaceOnly_ReturnsHex() =>
        MqttMessagePropertyDisplay.FormatCorrelationData("   "u8.ToArray()).Should().Be("0x202020 (3 bytes)");

    [Test]
    public void FormatCorrelationData_TextWithInteriorSpaces_StillReturnsText() =>
        MqttMessagePropertyDisplay.FormatCorrelationData("order 42"u8.ToArray()).Should().Be("order 42");

    [TestCase("order 42")]
    [TestCase("order  42")]
    [TestCase("  order 42")]
    public void FormatCorrelationData_InteriorAndLeadingSpaces_AreReturnedVerbatim(string text) =>
        MqttMessagePropertyDisplay.FormatCorrelationData(System.Text.Encoding.UTF8.GetBytes(text)).Should().Be(text);
}
