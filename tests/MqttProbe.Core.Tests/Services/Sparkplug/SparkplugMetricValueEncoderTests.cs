using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugMetricValueEncoderTests
{
    [TestCase("int8", "-128", 1u, 0xFFFFFF80u)]
    [TestCase("int8", "127", 1u, 0x7Fu)]
    [TestCase("int16", "-32768", 2u, 0xFFFF8000u)]
    [TestCase("int16", "32767", 2u, 0x7FFFu)]
    [TestCase("int32", "-2147483648", 3u, 0x80000000u)]
    [TestCase("int32", "2147483647", 3u, 0x7FFFFFFFu)]
    [TestCase("int32", "-1", 3u, 0xFFFFFFFFu)]
    [TestCase("uint8", "0", 5u, 0u)]
    [TestCase("uint8", "255", 5u, 255u)]
    [TestCase("uint16", "65535", 6u, 65535u)]
    [TestCase("uint32", "4294967295", 7u, 4294967295u)]
    public void TryEncode_NarrowIntegers_UseIntValue(string dataType, string text, uint datatype, uint expected)
    {
        var metric = Encode(dataType, text);

        metric.Datatype.Should().Be(datatype);
        metric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.IntValue);
        metric.IntValue.Should().Be(expected);
    }

    [TestCase("int64", "-9223372036854775808", 4u, 0x8000000000000000UL)]
    [TestCase("int64", "9223372036854775807", 4u, 0x7FFFFFFFFFFFFFFFUL)]
    [TestCase("int64", "-1", 4u, 0xFFFFFFFFFFFFFFFFUL)]
    [TestCase("uint64", "9223372036854775808", 8u, 9223372036854775808UL)]
    [TestCase("uint64", "18446744073709551615", 8u, 18446744073709551615UL)]
    [TestCase("datetime", "1714050000000", 13u, 1714050000000UL)]
    public void TryEncode_WideIntegersAndDateTime_UseLongValue(
        string dataType, string text, uint datatype, ulong expected)
    {
        var metric = Encode(dataType, text);

        metric.Datatype.Should().Be(datatype);
        metric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.LongValue);
        metric.LongValue.Should().Be(expected);
    }

    [TestCase("int8", "-129")]
    [TestCase("int8", "128")]
    [TestCase("int16", "-32769")]
    [TestCase("int16", "32768")]
    [TestCase("int32", "-2147483649")]
    [TestCase("int32", "2147483648")]
    [TestCase("int64", "9223372036854775808")]
    [TestCase("int64", "-9223372036854775809")]
    [TestCase("uint8", "-1")]
    [TestCase("uint8", "256")]
    [TestCase("uint16", "65536")]
    [TestCase("uint32", "4294967296")]
    [TestCase("uint64", "18446744073709551616")]
    [TestCase("uint64", "-1")]
    [TestCase("int32", "1.5")]
    [TestCase("int32", "")]
    [TestCase("int32", "twelve")]
    [TestCase("float", "1,5")]
    [TestCase("float", "1e39")]
    [TestCase("float", "NaN")]
    [TestCase("double", "1e309")]
    [TestCase("double", "abc")]
    [TestCase("bool", "yes")]
    [TestCase("datetime", "2026-11-10T00:00:00Z")]
    [TestCase("datetime", "-1")]
    [TestCase("uuid", "not-a-uuid")]
    public void TryEncode_RejectsOutOfRangeOrUnparseableInput(string dataType, string text)
    {
        SparkplugMetricValueEncoder.TryEncode(dataType, text, out var metric, out var error).Should().BeFalse();

        metric.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void TryEncode_Float_UsesFloatValue()
    {
        var metric = Encode("float", "0.1");

        metric.Datatype.Should().Be(9u);
        metric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.FloatValue);
        metric.FloatValue.Should().Be(0.1f);
    }

    [Test]
    public void TryEncode_Double_UsesDoubleValueAtFullPrecision()
    {
        var metric = Encode("double", "0.12345678901234567");

        metric.Datatype.Should().Be(10u);
        metric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.DoubleValue);
        metric.DoubleValue.Should().Be(0.12345678901234567d);
    }

    [TestCase("true", true)]
    [TestCase("False", false)]
    public void TryEncode_Bool_UsesBooleanValue(string text, bool expected)
    {
        var metric = Encode("bool", text);

        metric.Datatype.Should().Be(11u);
        metric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.BooleanValue);
        metric.BooleanValue.Should().Be(expected);
    }

    [TestCase("string", 12u)]
    [TestCase("text", 14u)]
    public void TryEncode_StringTypes_PreserveTextVerbatim(string dataType, uint datatype)
    {
        const string text = "  {\"name\": \"Zoë ☃\",\n  \"quoted\": \"a\\\"b\"}\t ";

        var metric = Encode(dataType, text);

        metric.Datatype.Should().Be(datatype);
        metric.ValueCase.Should().Be(Payload.Types.Metric.ValueOneofCase.StringValue);
        metric.StringValue.Should().Be(text);
    }

    [Test]
    public void TryEncode_EmptyString_IsValid()
    {
        Encode("string", "").StringValue.Should().BeEmpty();
    }

    [Test]
    public void TryEncode_Uuid_UsesTrimmedStringValue()
    {
        var metric = Encode("uuid", " 123e4567-e89b-12d3-a456-426614174000 ");

        metric.Datatype.Should().Be(15u);
        metric.StringValue.Should().Be("123e4567-e89b-12d3-a456-426614174000");
    }

    [TestCase("unknown")]
    [TestCase("")]
    [TestCase("bytes")]
    [TestCase("dataset")]
    [TestCase("template")]
    [TestCase("file")]
    [TestCase("propertyset")]
    [TestCase("propertysetlist")]
    [TestCase("int32[]")]
    [TestCase("string[]")]
    [TestCase("boolean")]
    public void UnsupportedTypes_AreNotWritable(string dataType)
    {
        SparkplugMetricValueEncoder.IsWritable(dataType).Should().BeFalse();
        SparkplugMetricValueEncoder.TryEncode(dataType, "1", out var metric, out var error).Should().BeFalse();
        metric.Should().BeNull();
        error.Should().Contain("cannot be written");
    }

    private static Payload.Types.Metric Encode(string dataType, string text)
    {
        SparkplugMetricValueEncoder.TryEncode(dataType, text, out var metric, out var error)
            .Should().BeTrue(error);
        return metric!;
    }
}
