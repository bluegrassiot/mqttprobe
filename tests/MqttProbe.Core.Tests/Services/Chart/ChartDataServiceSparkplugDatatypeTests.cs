using Google.Protobuf;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.Tests.Utilities;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceSparkplugDatatypeTests
{
    private IMqttManagedClient _client = null!;
    private ChartDataService? _service;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        _client = Substitute.For<IMqttManagedClient>();
        _handler = null;
        _client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());
    }

    [TearDown]
    public void TearDown()
    {
        _service?.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task OngoingNodeData_NamedDatetimeLongValue_CollectsThroughNamedPath()
    {
        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Timestamp" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true));

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Name = "Timestamp", Datatype = 14, LongValue = 1714050000000 }));

        _service!.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1714050000000, 0.001);
    }

    [Test]
    public async Task OngoingNodeAndDeviceData_AliasOnlyNativeMetricsWithoutDatatype_CollectThroughNamedPaths()
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
                Metrics = [new MetricSnapshot { Name = "Flow Rate", DataType = "double", Value = "0", Alias = 42 }]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1",
                Metrics = [new MetricSnapshot { Name = "Running", DataType = "bool", Value = "false", Alias = 7 }]
            }
        ]);

        var nodeSeriesId = Guid.NewGuid();
        var deviceSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = nodeSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" },
            new ChartSeries
            {
                Id = deviceSeriesId,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Running"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", SpbPayload(
            new Payload.Types.Metric { Alias = 42, DoubleValue = 24.5 }));
        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", SpbPayload(
            new Payload.Types.Metric { Alias = 7, BooleanValue = true }));

        _service!.GetPoints(nodeSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
        _service.GetPoints(deviceSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1.0, 0.001);
    }

    private async Task BuildServiceAsync(
        IChartSettings chartSettings,
        ISparkplugSettings sparkplugSettings,
        ISparkplugTopologyService? topology = null)
    {
        _service = new ChartDataService(
            _client,
            new JsonFieldExtractor(),
            new ChartFieldRegistry(),
            chartSettings,
            TestPipelineHelper.BuildBuiltInPipeline(),
            sparkplugSettings,
            topology);
        await _service.StartAsync();
    }

    private Task FireAsync(string topic, byte[] payload) => _handler!(MakeBinaryArgs(topic, payload));

    private static IChartSettings SettingsWith(params ChartSeries[] series)
    {
        var settings = Substitute.For<IChartSettings>();
        settings.GetCharts(Arg.Any<Guid>()).Returns(
        [
            new ChartConfiguration { MaxPoints = 100, Series = [.. series] }
        ]);
        return settings;
    }

    private static ISparkplugSettings SparkplugWith(bool enrichAliasNames)
    {
        var settings = Substitute.For<ISparkplugSettings>();
        settings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = enrichAliasNames });
        return settings;
    }

    private static byte[] SpbPayload(params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.AddRange(metrics);
        return payload.ToByteArray();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeBinaryArgs(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }
}
