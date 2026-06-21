using MqttProbe.Models.Emulation;
using MqttProbe.Services.Emulation;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class EmulationRateCalculatorTests
{
    private static EmulatorDeviceConfig Device(int metricCount)
    {
        var device = new EmulatorDeviceConfig();
        for (var i = 0; i < metricCount; i++)
            device.Metrics.Add(new EmulatorMetricConfig { Name = $"Metric-{i + 1}" });
        return device;
    }

    private static EmulatorNodeConfig Node(
        EmulatorNodeType type,
        GenericPayloadFormat format = GenericPayloadFormat.Json,
        params int[] deviceMetricCounts)
    {
        var node = new EmulatorNodeConfig { Type = type, PayloadFormat = format };
        foreach (var count in deviceMetricCounts)
            node.Devices.Add(Device(count));
        return node;
    }

    [Test]
    public void ProjectedMessagesPerSecond_SparkplugNode_CountsNdataPlusDevicesWithMetrics()
    {
        var nodes = new[] { Node(EmulatorNodeType.SparkplugB, deviceMetricCounts: [3, 2, 0]) };

        var rate = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 500);

        rate.Should().Be((1 + 2) * 2.0);
    }

    [Test]
    public void ProjectedMessagesPerSecond_SparkplugNodeWithoutDevices_CountsNdataOnly()
    {
        var nodes = new[] { Node(EmulatorNodeType.SparkplugB) };

        var rate = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 1000);

        rate.Should().Be(1.0);
    }

    [Test]
    public void ProjectedMessagesPerSecond_GenericJson_CountsDevicesWithMetrics()
    {
        var nodes = new[] { Node(EmulatorNodeType.Generic, GenericPayloadFormat.Json, 3, 2, 0) };

        var rate = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 1000);

        rate.Should().Be(2.0);
    }

    [Test]
    public void ProjectedMessagesPerSecond_GenericPlainText_CountsEveryMetric()
    {
        var nodes = new[] { Node(EmulatorNodeType.Generic, GenericPayloadFormat.PlainText, 3, 2) };

        var rate = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 500);

        rate.Should().Be(5 * 2.0);
    }

    [Test]
    public void ProjectedMessagesPerSecond_GenericHex_CountsEveryMetric()
    {
        var nodes = new[] { Node(EmulatorNodeType.Generic, GenericPayloadFormat.Hex, 4) };

        var rate = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 1000);

        rate.Should().Be(4.0);
    }

    [Test]
    public void ProjectedMessagesPerSecond_MixedFleet_SumsPerNodeContributions()
    {
        var nodes = new[]
        {
            Node(EmulatorNodeType.SparkplugB, deviceMetricCounts: [1]),
            Node(EmulatorNodeType.Generic, GenericPayloadFormat.Json, 1, 1),
            Node(EmulatorNodeType.Generic, GenericPayloadFormat.Hex, 2, 3)
        };

        var rate = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 1000);

        rate.Should().Be(2 + 2 + 5);
    }

    [Test]
    public void ProjectedMessagesPerSecond_FasterInterval_ScalesLinearly()
    {
        var nodes = new[] { Node(EmulatorNodeType.SparkplugB, deviceMetricCounts: [1]) };

        var at500 = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 500);
        var at250 = EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 250);

        at250.Should().Be(at500 * 2);
    }

    [Test]
    public void ProjectedMessagesPerSecond_NonPositiveInterval_ReturnsZero()
    {
        var nodes = new[] { Node(EmulatorNodeType.SparkplugB, deviceMetricCounts: [1]) };

        EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, 0).Should().Be(0);
        EmulationRateCalculator.ProjectedMessagesPerSecond(nodes, -100).Should().Be(0);
    }

    [Test]
    public void IsHighThroughput_AtOrAboveThreshold_IsTrue()
    {
        EmulationRateCalculator.IsHighThroughput(200).Should().BeTrue();
        EmulationRateCalculator.IsHighThroughput(350.5).Should().BeTrue();
    }

    [Test]
    public void IsHighThroughput_BelowThreshold_IsFalse()
    {
        EmulationRateCalculator.IsHighThroughput(199.99).Should().BeFalse();
        EmulationRateCalculator.IsHighThroughput(0).Should().BeFalse();
    }
}
