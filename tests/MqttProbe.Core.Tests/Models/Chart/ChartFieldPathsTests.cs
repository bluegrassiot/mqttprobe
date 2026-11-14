using MqttProbe.Core.Models.Chart;

namespace MqttProbe.Core.Tests.Models.Chart;

[TestFixture]
public sealed class ChartFieldPathsTests
{
    [Test]
    public void Root_IsDollarSentinel() =>
        ChartFieldPaths.Root.Should().Be("$");

    [TestCase("0", 0.0)]
    [TestCase("0.00", 0.0)]
    [TestCase("42", 42.0)]
    [TestCase("-5.5", -5.5)]
    [TestCase("1.5e2", 150.0)]
    [TestCase(" 21.5 ", 21.5)]
    [TestCase("\"22.4\"", 22.4)]
    [TestCase("\" 1.5 \"", 1.5)]
    public void TryParseRootNumber_AcceptsNumericScalars(string payload, double expected)
    {
        ChartFieldPaths.TryParseRootNumber(payload, out var value).Should().BeTrue();
        value.Should().BeApproximately(expected, 0.001);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("null")]
    [TestCase("true")]
    [TestCase("false")]
    [TestCase("{}")]
    [TestCase("[1]")]
    [TestCase("junk")]
    [TestCase("1,5")]
    [TestCase("1,000")]
    [TestCase("NaN")]
    [TestCase("Infinity")]
    [TestCase("-Infinity")]
    [TestCase("1e400")]
    [TestCase("\"\"")]
    [TestCase("\"abc\"")]
    [TestCase("\"NaN\"")]
    [TestCase("\"1,000\"")]
    public void TryParseRootNumber_RejectsNonScalarOrNonFinitePayloads(string payload)
    {
        ChartFieldPaths.TryParseRootNumber(payload, out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    [Test]
    public void TryParseRootNumber_NullPayload_IsRejected()
    {
        ChartFieldPaths.TryParseRootNumber(null, out var value).Should().BeFalse();
        value.Should().Be(0);
    }
}
