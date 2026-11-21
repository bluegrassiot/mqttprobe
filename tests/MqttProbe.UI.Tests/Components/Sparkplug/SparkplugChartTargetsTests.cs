using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugChartTargetsTests
{
    [Test]
    public void JsonPath_PreservesExactMetricName()
    {
        SparkplugChartTargets.JsonPath("Flow Rate (L/min)")
            .Should().Be("metrics.Flow Rate (L/min)");
    }

    [Test]
    public void IsChartable_UsesNativeTypeNotValueShape()
    {
        var booleanMetric = new SpbMetricSnapshot("Pump Enabled", "bool", "true", DateTime.UtcNow);
        var numericMetric = new SpbMetricSnapshot("Flow Rate", "double", "12.5", DateTime.UtcNow);
        var numericStringMetric = new SpbMetricSnapshot("Batch Count", "string", "42", DateTime.UtcNow);
        var booleanStringMetric = new SpbMetricSnapshot("Enabled Text", "string", "true", DateTime.UtcNow);

        SparkplugNodeDisplay.IsNumericType(booleanMetric.DataType).Should().BeFalse();
        SparkplugNodeDisplay.IsNumericType(numericStringMetric.DataType).Should().BeFalse();
        SparkplugChartTargets.IsChartable(booleanMetric).Should().BeTrue();
        SparkplugChartTargets.IsChartable(numericMetric).Should().BeTrue();
        SparkplugChartTargets.IsChartable(numericStringMetric).Should().BeFalse();
        SparkplugChartTargets.IsChartable(booleanStringMetric).Should().BeFalse();
    }

    [Test]
    public void IsChartable_AllowsDatetimeLongValue_WithoutWideningNumericColorTypes()
    {
        SparkplugNodeDisplay.IsNumericType("datetime").Should().BeFalse();
        Chartable("datetime", "1712345678000").Should().BeTrue("datetime longValue extracts as a number");
        Chartable("datetime", "\u2014").Should().BeFalse("a datetime without a value extracts nothing");
        Chartable("datetime", "true").Should().BeFalse("datetime values extract as numbers, not booleans");
    }

    [Test]
    public void IsChartable_MatchesSparkplugChartExtraction()
    {
        Chartable("double", "20.0000").Should().BeTrue("native numeric values extract as numbers");
        Chartable("int64", "42").Should().BeTrue("native integer values extract as numbers");
        Chartable("bool", "true").Should().BeTrue("native boolean values extract as 1 and 0");
        Chartable("string", "42").Should().BeFalse("string_value is not a metric value field");
        Chartable("string", "true").Should().BeFalse("string_value is not a metric value field");
        Chartable("string", "running").Should().BeFalse("string_value has no chart value");
        Chartable("bool", "\u2014").Should().BeFalse("a metric without a value extracts nothing");
    }

    private static bool Chartable(string dataType, string value) =>
        SparkplugChartTargets.IsChartable(new SpbMetricSnapshot("Metric", dataType, value, DateTime.UtcNow));
}
