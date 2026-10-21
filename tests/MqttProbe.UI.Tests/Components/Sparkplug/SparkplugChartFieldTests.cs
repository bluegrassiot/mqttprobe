using MqttProbe.Core.Models.Sparkplug;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugChartFieldTests
{
    [Test]
    public void NodeTopic_UsesOngoingNodeDataTopic() =>
        SparkplugChartField.NodeTopic("Plant1", "Node-2").Should().Be("spBv1.0/Plant1/NDATA/Node-2");

    [Test]
    public void DeviceTopic_UsesOngoingDeviceDataTopic() =>
        SparkplugChartField.DeviceTopic("Plant1", "Node-2", "Device-1")
            .Should().Be("spBv1.0/Plant1/DDATA/Node-2/Device-1");

    [TestCase("Temp", ExpectedResult = "metrics.Temp")]
    [TestCase("Motor Speed", ExpectedResult = "metrics.Motor Speed")]
    [TestCase("motor speed", ExpectedResult = "metrics.motor speed")]
    [TestCase("Pressure 1", ExpectedResult = "metrics.Pressure 1")]
    public string JsonPath_KeepsMetricNameVerbatim(string metricName) =>
        SparkplugChartField.JsonPath(metricName);

    [TestCase("int8", "12")]
    [TestCase("int64", "12")]
    [TestCase("uint32", "12")]
    [TestCase("uint64", "12")]
    [TestCase("float", "12.0000")]
    [TestCase("double", "12.0000")]
    [TestCase("bool", "true")]
    [TestCase("boolean", "false")]
    public void IsChartable_NumericAndBooleanTypes_AreEligible(string dataType, string value) =>
        SparkplugChartField.IsChartable(Metric("Temp", dataType, value)).Should().BeTrue();

    [TestCase("string", "42")]
    [TestCase("string", "not a number")]
    [TestCase("text", "42.5")]
    public void IsChartable_StringTypes_AreNotEligible_EvenWhenTheValueLooksNumeric(
        string dataType, string value) =>
        SparkplugChartField.IsChartable(Metric("Temp", dataType, value)).Should().BeFalse(
            "a Sparkplug string metric travels as stringValue, which the chart collector does not read");

    [TestCase("bytes")]
    [TestCase("datetime")]
    [TestCase("uuid")]
    [TestCase("dataset")]
    [TestCase("file")]
    [TestCase("template")]
    [TestCase("unknown")]
    public void IsChartable_NonNumericTypes_AreNotEligible(string dataType) =>
        SparkplugChartField.IsChartable(Metric("Temp", dataType, "12")).Should().BeFalse();

    private static SpbMetricSnapshot Metric(string name, string dataType, string value) =>
        new(name, dataType, value, DateTime.UtcNow);
}
