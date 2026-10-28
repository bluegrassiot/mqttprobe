using MqttProbe.UI.Utilities;

namespace MqttProbe.UI.Tests.Utilities;

[TestFixture]
public class CorrelationDataCodecTests
{
    [TestCase(null)]
    [TestCase("")]
    public void TryParse_Empty_IsAbsent(string? text)
    {
        CorrelationDataCodec.TryParse(text, CorrelationDataEncoding.Hex, out var bytes).Should().BeTrue();
        bytes.Should().BeEmpty();
    }

    [Test]
    public void TryParse_Text_KeepsWhitespaceAsUtf8() =>
        CorrelationDataCodec.TryParse("  order  42 ", CorrelationDataEncoding.Text, out var bytes).Should().BeTrue()
            .And.Subject.Should().Be(true, "text always parses");

    [Test]
    public void TryParse_Text_EncodesUtf8()
    {
        CorrelationDataCodec.TryParse("  order  42 ", CorrelationDataEncoding.Text, out var bytes);
        bytes.Should().Equal("  order  42 "u8.ToArray());
    }

    [TestCase("abcd", new byte[] { 0xAB, 0xCD })]
    [TestCase("0xABCD", new byte[] { 0xAB, 0xCD })]
    [TestCase("ab cd ef", new byte[] { 0xAB, 0xCD, 0xEF })]
    [TestCase("ab:cd-ef", new byte[] { 0xAB, 0xCD, 0xEF })]
    [TestCase(" 0X00ff ", new byte[] { 0x00, 0xFF })]
    public void TryParse_Hex_AcceptsCommonForms(string text, byte[] expected)
    {
        CorrelationDataCodec.TryParse(text, CorrelationDataEncoding.Hex, out var bytes).Should().BeTrue();
        bytes.Should().Equal(expected);
    }

    [TestCase("abc")]
    [TestCase("zz")]
    [TestCase("0x")]
    [TestCase("12 3")]
    public void TryParse_Hex_RejectsOddOrNonHex(string text)
    {
        CorrelationDataCodec.TryParse(text, CorrelationDataEncoding.Hex, out var bytes).Should().BeFalse();
        bytes.Should().BeEmpty();
    }

    [Test]
    public void Format_PrintableUtf8_ReturnsText() =>
        CorrelationDataCodec.Format("request-7"u8.ToArray()).Should().Be((CorrelationDataEncoding.Text, "request-7"));

    [Test]
    public void Format_BinaryBytes_ReturnsHex() =>
        CorrelationDataCodec.Format([0x00, 0xFF, 0x10]).Should().Be((CorrelationDataEncoding.Hex, "00ff10"));

    [TestCase(null)]
    [TestCase(new byte[0])]
    public void Format_Empty_ReturnsEmptyText(byte[]? bytes) =>
        CorrelationDataCodec.Format(bytes).Should().Be((CorrelationDataEncoding.Text, string.Empty));

    [Test]
    public void Format_ThenParse_RoundTripsBinary()
    {
        byte[] original = [0xC3, 0x28, 0x00, 0x7F];
        var (encoding, text) = CorrelationDataCodec.Format(original);

        CorrelationDataCodec.TryParse(text, encoding, out var bytes).Should().BeTrue();
        bytes.Should().Equal(original);
    }
}
