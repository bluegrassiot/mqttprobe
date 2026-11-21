using System.Globalization;
using MqttProbe.Core.Services.Sparkplug;

namespace MqttProbe.Core.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugMetricValueConverterTests
{
    [TestCase(1, 0x80UL, "-128")]
    [TestCase(1, 0x7FUL, "127")]
    [TestCase(1, 0xFFUL, "-1")]
    [TestCase(1, 0xFFFFFFFFUL, "-1")]
    [TestCase(1, 0x1FFUL, "-1")]
    [TestCase(2, 0x8000UL, "-32768")]
    [TestCase(2, 0x7FFFUL, "32767")]
    [TestCase(2, 0xFFFFUL, "-1")]
    [TestCase(2, 0xFFFFFFFFUL, "-1")]
    [TestCase(2, 0x1FFFFUL, "-1")]
    [TestCase(3, 0x80000000UL, "-2147483648")]
    [TestCase(3, 0x7FFFFFFFUL, "2147483647")]
    [TestCase(3, 0xFFFFFFFFUL, "-1")]
    [TestCase(4, 0x8000000000000000UL, "-9223372036854775808")]
    [TestCase(4, 0x7FFFFFFFFFFFFFFFUL, "9223372036854775807")]
    [TestCase(4, 0xFFFFFFFFFFFFFFFFUL, "-1")]
    public void DecodeInteger_SignedTypes_ReinterpretsNarrowBits(int datatype, ulong rawValue, string expected)
    {
        SparkplugMetricValueConverter.DecodeInteger((uint)datatype, rawValue)
            .ToString(CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [TestCase(5, 255UL, "255")]
    [TestCase(5, 0xFFFFFFFFFFFFFFFFUL, "255")]
    [TestCase(6, 65535UL, "65535")]
    [TestCase(6, 0xFFFFFFFFFFFFFFFFUL, "65535")]
    [TestCase(7, 4294967295UL, "4294967295")]
    [TestCase(8, 18446744073709551615UL, "18446744073709551615")]
    public void DecodeInteger_UnsignedTypes_PreservesValue(int datatype, ulong rawValue, string expected)
    {
        SparkplugMetricValueConverter.DecodeInteger((uint)datatype, rawValue)
            .ToString(CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [TestCase(0, 0xFFFFFFFFFFFFFFFFUL)]
    [TestCase(13, 1714050000000UL)]
    [TestCase(14, 0xFFFFFFFFFFFFFFFFUL)]
    [TestCase(99, 42UL)]
    public void DecodeInteger_NonIntegerTypes_PreservesRawValue(int datatype, ulong rawValue)
    {
        SparkplugMetricValueConverter.DecodeInteger((uint)datatype, rawValue).Should().Be(rawValue);
    }

    [TestCase(0, "unknown")]
    [TestCase(1, "int8")]
    [TestCase(8, "uint64")]
    [TestCase(13, "datetime")]
    [TestCase(14, "text")]
    [TestCase(20, "propertyset")]
    [TestCase(21, "propertysetlist")]
    [TestCase(22, "int8[]")]
    [TestCase(34, "datetime[]")]
    [TestCase(99, "unknown")]
    public void LabelForDatatype_ReturnsCanonicalLabel(int datatype, string expected)
    {
        SparkplugMetricValueConverter.LabelForDatatype((uint)datatype).Should().Be(expected);
    }

    [TestCase("unknown", 0)]
    [TestCase("int8", 1)]
    [TestCase("uint64", 8)]
    [TestCase("double", 10)]
    [TestCase("text", 14)]
    [TestCase("propertysetlist", 21)]
    [TestCase("int8[]", 22)]
    [TestCase("datetime[]", 34)]
    public void DatatypeForLabel_ReturnsCanonicalDatatype(string label, int expected)
    {
        SparkplugMetricValueConverter.DatatypeForLabel(label).Should().Be((uint)expected);
    }

    [Test]
    public void DatatypeForLabel_UnknownLabel_ReturnsNull()
    {
        SparkplugMetricValueConverter.DatatypeForLabel("not-a-datatype").Should().BeNull();
    }

    [Test]
    public void DatatypeForLabel_RoundTripsEveryCanonicalDatatype()
    {
        for (uint datatype = 1; datatype <= 34; datatype++)
        {
            SparkplugMetricValueConverter.DatatypeForLabel(
                    SparkplugMetricValueConverter.LabelForDatatype(datatype))
                .Should().Be(datatype);
        }
    }

    [TestCase(1, true)]
    [TestCase(8, true)]
    [TestCase(0, false)]
    [TestCase(9, false)]
    [TestCase(13, false)]
    [TestCase(22, false)]
    public void IsIntegerDatatype_OnlyAcceptsIntegerDatatypes(int datatype, bool expected)
    {
        SparkplugMetricValueConverter.IsIntegerDatatype((uint)datatype).Should().Be(expected);
    }
}
