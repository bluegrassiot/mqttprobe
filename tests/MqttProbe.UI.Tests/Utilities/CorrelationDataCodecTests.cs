using MqttProbe.Core.Services.Mqtt;
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


    [Test]
    public void TryParse_Text_AcceptsExactly65535Bytes()
    {
        CorrelationDataCodec.TryParse(new string('a', MqttStringRules.MaxBinaryBytes), CorrelationDataEncoding.Text, out var bytes, out var error).Should().BeTrue();
        bytes.Should().HaveCount(MqttStringRules.MaxBinaryBytes);
        error.Should().BeNull();
    }

    [Test]
    public void TryParse_Text_Rejects65536Bytes()
    {
        CorrelationDataCodec.TryParse(new string('a', MqttStringRules.MaxBinaryBytes + 1), CorrelationDataEncoding.Text, out var bytes, out var error).Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Be(CorrelationDataCodec.LengthError);
    }

    [Test]
    public void TryParse_Text_CountsUtf8BytesNotCharacters()
    {
        var euros = MqttStringRules.MaxBinaryBytes / 3;

        CorrelationDataCodec.TryParse(string.Concat(Enumerable.Repeat("€", euros)), CorrelationDataEncoding.Text, out _, out var ok).Should().BeTrue();
        ok.Should().BeNull();
        CorrelationDataCodec.TryParse(string.Concat(Enumerable.Repeat("€", euros + 1)), CorrelationDataEncoding.Text, out _, out var error).Should().BeFalse();
        error.Should().Be(CorrelationDataCodec.LengthError);
    }

    [Test]
    public void TryParse_Hex_Boundary()
    {
        CorrelationDataCodec.TryParse(new string('f', MqttStringRules.MaxBinaryBytes * 2), CorrelationDataEncoding.Hex, out var bytes, out var ok).Should().BeTrue();
        bytes.Should().HaveCount(MqttStringRules.MaxBinaryBytes);
        ok.Should().BeNull();

        CorrelationDataCodec.TryParse(new string('f', (MqttStringRules.MaxBinaryBytes + 1) * 2), CorrelationDataEncoding.Hex, out bytes, out var error).Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Be(CorrelationDataCodec.LengthError);
    }

    [Test]
    public void TryParse_Hex_ReportsHexErrorBeforeLength()
    {
        CorrelationDataCodec.TryParse("abc", CorrelationDataEncoding.Hex, out _, out var error).Should().BeFalse();
        error.Should().Be(CorrelationDataCodec.HexError);
    }
}
