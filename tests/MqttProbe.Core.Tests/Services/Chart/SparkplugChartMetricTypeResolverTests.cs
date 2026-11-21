using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class SparkplugChartMetricTypeResolverTests
{
    [Test]
    public async Task Resolve_NodeAlias_ReturnsNodeType()
    {
        var topology = await TopologyWithNodeAndDeviceAsync();

        var resolver = new SparkplugChartMetricTypeResolver("spBv1.0/g/NDATA/eon1", topology);

        resolver.Resolve(7, null).Should().Be(1u);
    }

    [Test]
    public async Task Resolve_DeviceAlias_ReturnsDeviceTypeNotNodeType()
    {
        var topology = await TopologyWithNodeAndDeviceAsync();

        var deviceResolver = new SparkplugChartMetricTypeResolver("spBv1.0/g/DDATA/eon1/dev1", topology);
        var nodeResolver = new SparkplugChartMetricTypeResolver("spBv1.0/g/NDATA/eon1", topology);

        deviceResolver.Resolve(7, null).Should().Be(5u);
        nodeResolver.Resolve(7, null).Should().Be(1u);
    }

    [Test]
    public async Task Resolve_NameWithoutAlias_ReturnsType()
    {
        var topology = await TopologyWithNodeAndDeviceAsync();

        var resolver = new SparkplugChartMetricTypeResolver("spBv1.0/g/NDATA/eon1", topology);

        resolver.Resolve(0, "Unsigned Level").Should().Be(6u);
    }

    [Test]
    public async Task Resolve_UnknownMetric_ReturnsNull()
    {
        var topology = await TopologyWithNodeAndDeviceAsync();

        var resolver = new SparkplugChartMetricTypeResolver("spBv1.0/g/NDATA/eon1", topology);

        resolver.Resolve(99, "Missing").Should().BeNull();
    }

    [Test]
    public void Resolve_NullTopologyOrNonMetricTopic_ReturnsNull()
    {
        new SparkplugChartMetricTypeResolver("spBv1.0/g/NDATA/eon1", null)
            .Resolve(7, "Level").Should().BeNull();
        new SparkplugChartMetricTypeResolver("spBv1.0/STATE/host", new SparkplugTopologyService())
            .Resolve(7, "Level").Should().BeNull();
    }

    [Test]
    public async Task Resolve_UnknownDatatypeName_ReturnsNull()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "unknown", Value = "0", Alias = 7 }]
            }
        ]);

        var resolver = new SparkplugChartMetricTypeResolver("spBv1.0/g/NDATA/eon1", topology);

        resolver.Resolve(7, "Level").Should().BeNull();
    }

    private static async Task<SparkplugTopologyService> TopologyWithNodeAndDeviceAsync()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/NBIRTH/eon1",
                GroupId = "g",
                NodeId = "eon1",
                Metrics =
                [
                    new MetricSnapshot { Name = "Level", DataType = "int8", Value = "0", Alias = 7 },
                    new MetricSnapshot { Name = "Unsigned Level", DataType = "uint16", Value = "0", Alias = 8 }
                ]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1",
                Metrics = [new MetricSnapshot { Name = "Level", DataType = "uint8", Value = "0", Alias = 7 }]
            }
        ]);

        return topology;
    }
}
