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
public class ChartDataServiceSparkplugSeriesTests
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
    public async Task OngoingNodeData_NamedFlowRateMetric_WritesOnlyExactNDataTopicSeries()
    {
        var nodeSeries = Guid.NewGuid();
        var deviceSeries = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = nodeSeries, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" },
            new ChartSeries { Id = deviceSeries, Topic = "spBv1.0/g/DDATA/eon1", JsonPath = "metrics.Flow Rate" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true));

        await FireAsync("spBv1.0/g/NDATA/eon1", PayloadWith(("Flow Rate", 0, 12.5)));

        _service!.GetPoints(nodeSeries).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(12.5, 0.001);
        _service.GetPoints(deviceSeries).Should().BeEmpty();
    }

    [Test]
    public async Task OngoingDeviceData_NamedFlowRateMetric_WritesOnlyExactDDataTopicSeries()
    {
        var deviceSeries = Guid.NewGuid();
        var nodeSeries = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries
            {
                Id = deviceSeries,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Flow Rate"
            },
            new ChartSeries { Id = nodeSeries, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true));

        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", PayloadWith(("Flow Rate", 0, 31.25)));

        _service!.GetPoints(deviceSeries).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(31.25, 0.001);
        _service.GetPoints(nodeSeries).Should().BeEmpty();
    }

    [Test]
    public async Task OngoingNodeData_AliasOnlyMetric_ResolvesFlowRateFromPriorNodeBirth()
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
            }
        ]);

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", PayloadWith((null, 42, 24.5)));

        _service!.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task OngoingDeviceData_AliasOnlyMetric_ResolvesFlowRateFromPriorDeviceBirthMap()
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
                Metrics = [new MetricSnapshot { Name = "Node Rate", DataType = "double", Value = "0", Alias = 42 }]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1",
                Metrics = [new MetricSnapshot { Name = "Flow Rate", DataType = "double", Value = "0", Alias = 42 }]
            }
        ]);

        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries
            {
                Id = seriesId,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Flow Rate"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", PayloadWith((null, 42, 24.5)));

        _service!.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task OngoingNodeData_MixedNamedAndAliasOnlyMetrics_CollectsBothPaths()
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
                Metrics = [new MetricSnapshot { Name = "Pressure", DataType = "double", Value = "0", Alias = 7 }]
            }
        ]);

        var flowSeriesId = Guid.NewGuid();
        var pressureSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = flowSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" },
            new ChartSeries { Id = pressureSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Pressure" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", PayloadWith(("Flow Rate", 0, 7.25), (null, 7, 42.75)));

        _service!.GetPoints(flowSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(7.25, 0.001);
        _service.GetPoints(pressureSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(42.75, 0.001);
    }

    [Test]
    public async Task OngoingNodeData_AliasOnlyMetric_WithEnrichmentDisabled_LeavesNamedSeriesEmptyButIndexedPathCollects()
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
            }
        ]);

        var namedSeriesId = Guid.NewGuid();
        var indexedSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries
            {
                Id = namedSeriesId,
                Topic = "spBv1.0/g/NDATA/eon1",
                JsonPath = "metrics.Flow Rate"
            },
            new ChartSeries
            {
                Id = indexedSeriesId,
                Topic = "spBv1.0/g/NDATA/eon1",
                JsonPath = "metrics[0].doubleValue"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: false), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", PayloadWith((null, 42, 24.5)));

        _service!.GetPoints(namedSeriesId).Should().BeEmpty();
        _service.GetPoints(indexedSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task OngoingNodeData_NamedFlowRateMetric_WrongCasePath_DoesNotCollect()
    {
        var seriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = seriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.flow rate" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true));

        await FireAsync("spBv1.0/g/NDATA/eon1", PayloadWith(("Flow Rate", 0, 12.5)));

        _service!.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task OngoingNodeData_NamedStringMetrics_DoNotCollectThroughNamedPaths()
    {
        var flowSeriesId = Guid.NewGuid();
        var onlineSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = flowSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" },
            new ChartSeries { Id = onlineSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Online" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true));

        await FireAsync("spBv1.0/g/NDATA/eon1",
            StringPayloadWith(("Flow Rate", 0, "42"), ("Online", 0, "true")));

        _service!.GetPoints(flowSeriesId).Should().BeEmpty();
        _service.GetPoints(onlineSeriesId).Should().BeEmpty();
    }

    [Test]
    public async Task OngoingNodeData_AliasOnlyStringMetrics_LeaveNamedPathsEmptyButNumericTextIndexedPathCollects()
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
                    new MetricSnapshot { Name = "Flow Rate", DataType = "string", Value = "42", Alias = 42 },
                    new MetricSnapshot { Name = "Online", DataType = "string", Value = "true", Alias = 43 }
                ]
            }
        ]);

        var flowSeriesId = Guid.NewGuid();
        var onlineSeriesId = Guid.NewGuid();
        var indexedFlowSeriesId = Guid.NewGuid();
        var indexedOnlineSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = flowSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Flow Rate" },
            new ChartSeries { Id = onlineSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Online" },
            new ChartSeries
            {
                Id = indexedFlowSeriesId,
                Topic = "spBv1.0/g/NDATA/eon1",
                JsonPath = "metrics[0].stringValue"
            },
            new ChartSeries
            {
                Id = indexedOnlineSeriesId,
                Topic = "spBv1.0/g/NDATA/eon1",
                JsonPath = "metrics[1].stringValue"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1",
            StringPayloadWith((null, 42, "42"), (null, 43, "true")));

        _service!.GetPoints(flowSeriesId).Should().BeEmpty();
        _service.GetPoints(onlineSeriesId).Should().BeEmpty();
        _service.GetPoints(indexedFlowSeriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(42, 0.001);
        _service.GetPoints(indexedOnlineSeriesId).Should().BeEmpty();
    }

    [Test]
    public async Task OngoingNodeData_NamedAndAliasOnlyBooleanMetrics_CollectOneAndZero()
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
                Metrics = [new MetricSnapshot { Name = "Running", DataType = "boolean", Value = "false", Alias = 42 }]
            }
        ]);

        var namedSeriesId = Guid.NewGuid();
        var aliasSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries { Id = namedSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Ready" },
            new ChartSeries { Id = aliasSeriesId, Topic = "spBv1.0/g/NDATA/eon1", JsonPath = "metrics.Running" });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/NDATA/eon1", BooleanPayloadWith(("Ready", 0, true), (null, 42, false)));
        await FireAsync("spBv1.0/g/NDATA/eon1", BooleanPayloadWith(("Ready", 0, false), (null, 42, true)));

        _service!.GetPoints(namedSeriesId).Select(point => point.Value).Should().Equal(1.0, 0.0);
        _service.GetPoints(aliasSeriesId).Select(point => point.Value).Should().Equal(0.0, 1.0);
    }

    [Test]
    public async Task OngoingDeviceData_NamedAndAliasOnlyBooleanMetrics_CollectOneAndZero()
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync(
        [
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/g/DBIRTH/eon1/dev1",
                GroupId = "g",
                NodeId = "eon1",
                DeviceId = "dev1",
                Metrics = [new MetricSnapshot { Name = "Running", DataType = "boolean", Value = "false", Alias = 42 }]
            }
        ]);

        var namedSeriesId = Guid.NewGuid();
        var aliasSeriesId = Guid.NewGuid();
        var settings = SettingsWith(
            new ChartSeries
            {
                Id = namedSeriesId,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Ready"
            },
            new ChartSeries
            {
                Id = aliasSeriesId,
                Topic = "spBv1.0/g/DDATA/eon1/dev1",
                JsonPath = "metrics.Running"
            });
        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);

        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", BooleanPayloadWith(("Ready", 0, true), (null, 42, false)));
        await FireAsync("spBv1.0/g/DDATA/eon1/dev1", BooleanPayloadWith(("Ready", 0, false), (null, 42, true)));

        _service!.GetPoints(namedSeriesId).Select(point => point.Value).Should().Equal(1.0, 0.0);
        _service.GetPoints(aliasSeriesId).Select(point => point.Value).Should().Equal(0.0, 1.0);
    }

    [Test]
    public async Task SettingsChange_WhenOnlyPresentationChanges_PreservesAliasResolvedSeriesPoints()
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
            }
        ]);

        var connectionId = Guid.NewGuid();
        var series = new ChartSeries
        {
            Id = Guid.NewGuid(),
            Topic = "spBv1.0/g/NDATA/eon1",
            JsonPath = "metrics.Flow Rate"
        };
        var config = new ChartConfiguration { MaxPoints = 100, Series = [series] };
        var settings = Substitute.For<IChartSettings>();
        settings.GetCharts(Arg.Any<Guid>()).Returns([config]);

        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true), topology);
        _service!.SetConnection(connectionId);
        await FireAsync("spBv1.0/g/NDATA/eon1", PayloadWith((null, 42, 24.5)));

        series.DisplayName = "Renamed";
        series.Color = "#000000";
        config.Type = ChartType.Area;

        settings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task SettingsChange_WhenSparkplugJsonPathChanges_ResetsOnlyThatSeries()
    {
        var connectionId = Guid.NewGuid();
        var changedSeries = new ChartSeries
        {
            Id = Guid.NewGuid(),
            Topic = "spBv1.0/g/NDATA/eon1",
            JsonPath = "metrics.Flow Rate"
        };
        var untouchedSeries = new ChartSeries
        {
            Id = Guid.NewGuid(),
            Topic = "spBv1.0/g/NDATA/eon1",
            JsonPath = "metrics.Pressure"
        };
        var config = new ChartConfiguration { MaxPoints = 100, Series = [changedSeries, untouchedSeries] };
        var settings = Substitute.For<IChartSettings>();
        settings.GetCharts(Arg.Any<Guid>()).Returns([config]);

        await BuildServiceAsync(settings, SparkplugWith(enrichAliasNames: true));
        _service!.SetConnection(connectionId);
        await FireAsync("spBv1.0/g/NDATA/eon1", PayloadWith(("Flow Rate", 0, 12.5), ("Pressure", 0, 7.25)));

        _service.GetPoints(changedSeries.Id).Should().ContainSingle();
        _service.GetPoints(untouchedSeries.Id).Should().ContainSingle();

        changedSeries.JsonPath = "metrics.Other";

        settings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(changedSeries.Id).Should().BeEmpty();
        _service.GetPoints(untouchedSeries.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(7.25, 0.001);
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

    private static byte[] PayloadWith(params (string? Name, ulong Alias, double Value)[] metrics)
    {
        var payload = new Payload { Timestamp = 2 };
        foreach (var (name, alias, value) in metrics)
        {
            var metric = new Payload.Types.Metric { Alias = alias, Datatype = 10, DoubleValue = value };
            if (name is not null)
                metric.Name = name;

            payload.Metrics.Add(metric);
        }

        return payload.ToByteArray();
    }

    private static byte[] StringPayloadWith(params (string? Name, ulong Alias, string Value)[] metrics)
    {
        var payload = new Payload { Timestamp = 2 };
        foreach (var (name, alias, value) in metrics)
        {
            var metric = new Payload.Types.Metric { Alias = alias, Datatype = 12, StringValue = value };
            if (name is not null)
                metric.Name = name;

            payload.Metrics.Add(metric);
        }

        return payload.ToByteArray();
    }

    private static byte[] BooleanPayloadWith(params (string? Name, ulong Alias, bool Value)[] metrics)
    {
        var payload = new Payload { Timestamp = 2 };
        foreach (var (name, alias, value) in metrics)
        {
            var metric = new Payload.Types.Metric { Alias = alias, Datatype = 11, BooleanValue = value };
            if (name is not null)
                metric.Name = name;

            payload.Metrics.Add(metric);
        }

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
