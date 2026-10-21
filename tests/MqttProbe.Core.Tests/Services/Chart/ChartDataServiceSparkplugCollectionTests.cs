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
public class ChartDataServiceSparkplugCollectionTests
{
    private const string GroupId = "g";
    private const string NodeId = "eon1";
    private const string DeviceId = "dev1";
    private const string MetricName = "Tank Level";
    private const ulong MetricAlias = 42;

    private IMqttManagedClient _client = null!;
    private ChartFieldRegistry _registry = null!;
    private IChartSettings _settings = null!;
    private ISparkplugSettings _sparkplugSettings = null!;
    private SparkplugTopologyService _topology = null!;
    private readonly Dictionary<Guid, List<ChartSeries>> _seriesByConnection = [];
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        _client = Substitute.For<IMqttManagedClient>();
        _registry = new ChartFieldRegistry();
        _settings = Substitute.For<IChartSettings>();
        _settings.GetCharts(Arg.Any<Guid>()).Returns([]);
        _sparkplugSettings = Substitute.For<ISparkplugSettings>();
        _sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        _topology = new SparkplugTopologyService();

        _client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    private static string NodeDataTopic => $"spBv1.0/{GroupId}/NDATA/{NodeId}";

    private static string DeviceDataTopic => $"spBv1.0/{GroupId}/DDATA/{NodeId}/{DeviceId}";

    private ChartDataService CreateService() =>
        new(_client,
            new JsonFieldExtractor(),
            _registry,
            _settings,
            TestPipelineHelper.BuildBuiltInPipeline(),
            _sparkplugSettings,
            _topology);

    private Guid ConfigureSeries(Guid connectionId, string topic, string jsonPath) =>
        ConfigureSeries(connectionId, (topic, jsonPath))[0];

    private Guid[] ConfigureSeries(Guid connectionId, params (string Topic, string JsonPath)[] definitions)
    {
        if (!_seriesByConnection.TryGetValue(connectionId, out var series))
        {
            series = [];
            _seriesByConnection[connectionId] = series;
        }

        var created = definitions
            .Select(definition => new ChartSeries
            {
                Id = Guid.NewGuid(),
                Topic = definition.Topic,
                JsonPath = definition.JsonPath
            })
            .ToArray();
        series.AddRange(created);
        _settings.GetCharts(connectionId).Returns(
        [
            new ChartConfiguration { MaxPoints = 100, Series = [.. series] }
        ]);
        return [.. created.Select(item => item.Id)];
    }

    private Task ApplyNodeBirthAsync() => _topology.ApplyTopologyEventsAsync(
    [
        new NodeBirthEvent
        {
            FormatId = "sparkplug-b",
            Topic = $"spBv1.0/{GroupId}/NBIRTH/{NodeId}",
            GroupId = GroupId,
            NodeId = NodeId,
            Metrics = [new MetricSnapshot { Name = MetricName, DataType = "double", Value = "1.0", Alias = MetricAlias }]
        }
    ]);

    private Task ApplyDeviceBirthAsync() => _topology.ApplyTopologyEventsAsync(
    [
        new DeviceBirthEvent
        {
            FormatId = "sparkplug-b",
            Topic = $"spBv1.0/{GroupId}/DBIRTH/{NodeId}/{DeviceId}",
            GroupId = GroupId,
            NodeId = NodeId,
            DeviceId = DeviceId,
            Metrics = [new MetricSnapshot { Name = MetricName, DataType = "double", Value = "1.0", Alias = MetricAlias }]
        }
    ]);

    private static byte[] AliasOnlyPayload(double value)
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.Add(new Payload.Types.Metric { Alias = MetricAlias, Datatype = 10, DoubleValue = value });
        return payload.ToByteArray();
    }

    private static byte[] NamedPayload(string name, double value)
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.Add(new Payload.Types.Metric { Name = name, Datatype = 10, DoubleValue = value });
        return payload.ToByteArray();
    }

    private Task Fire(string topic, byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return _handler!(new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null));
    }

    [Test]
    public async Task NodeDataUpdate_CollectsAliasOnlyMetricPoint_PreservingMetricNameCaseAndSpaces()
    {
        await ApplyNodeBirthAsync();
        var connectionId = Guid.NewGuid();
        var seriesId = ConfigureSeries(connectionId, NodeDataTopic, $"metrics.{MetricName}");

        using var service = CreateService();
        service.SetConnection(connectionId);
        await service.StartAsync();

        await Fire(NodeDataTopic, AliasOnlyPayload(24.5));

        service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
        _registry.GetFields(NodeDataTopic).Should().ContainSingle(
            field => field.JsonPath == $"metrics.{MetricName}" && field.LastValue == 24.5);
    }

    [Test]
    public async Task NodeDataUpdates_AccumulatePointsForTheOngoingSeries()
    {
        await ApplyNodeBirthAsync();
        var connectionId = Guid.NewGuid();
        var seriesId = ConfigureSeries(connectionId, NodeDataTopic, $"metrics.{MetricName}");

        using var service = CreateService();
        service.SetConnection(connectionId);
        await service.StartAsync();

        await Fire(NodeDataTopic, AliasOnlyPayload(24.5));
        await Fire(NodeDataTopic, AliasOnlyPayload(25.5));

        service.GetPoints(seriesId).Select(point => point.Value).Should().Equal(24.5, 25.5);
    }

    [Test]
    public async Task DeviceDataUpdate_CollectsAliasOnlyMetricPoint_FromDeviceBirthAliasMap()
    {
        await ApplyDeviceBirthAsync();
        var connectionId = Guid.NewGuid();
        var seriesId = ConfigureSeries(connectionId, DeviceDataTopic, $"metrics.{MetricName}");

        using var service = CreateService();
        service.SetConnection(connectionId);
        await service.StartAsync();

        await Fire(DeviceDataTopic, AliasOnlyPayload(24.5));

        service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task DeviceDataUpdate_WithoutDeviceBirth_CollectsNoPointForTheMetricName()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = ConfigureSeries(connectionId, DeviceDataTopic, $"metrics.{MetricName}");

        using var service = CreateService();
        service.SetConnection(connectionId);
        await service.StartAsync();

        await Fire(DeviceDataTopic, AliasOnlyPayload(24.5));

        service.GetPoints(seriesId).Should().BeEmpty(
            "an alias-only device metric has no name until the device birth populated the alias map");
    }

    [Test]
    public async Task MetricCarryingItsOwnName_CollectsPoint_WithoutAliasEnrichment()
    {
        _sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = false });
        var connectionId = Guid.NewGuid();
        var seriesId = ConfigureSeries(connectionId, NodeDataTopic, $"metrics.{MetricName}");

        using var service = CreateService();
        service.SetConnection(connectionId);
        await service.StartAsync();

        await Fire(NodeDataTopic, NamedPayload(MetricName, 24.5));

        service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task AliasOnlyMetric_WithoutAliasEnrichment_CollectsNoPointForTheMetricName()
    {
        _sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = false });
        await ApplyNodeBirthAsync();
        var connectionId = Guid.NewGuid();
        var seriesIds = ConfigureSeries(
            connectionId,
            (NodeDataTopic, $"metrics.{MetricName}"),
            (NodeDataTopic, "metrics[0].doubleValue"));
        var byName = seriesIds[0];
        var byIndex = seriesIds[1];

        using var service = CreateService();
        service.SetConnection(connectionId);
        await service.StartAsync();

        await Fire(NodeDataTopic, AliasOnlyPayload(24.5));

        service.GetPoints(byIndex).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
        service.GetPoints(byName).Should().BeEmpty(
            "the metric name of an alias-only metric is only known when alias enrichment is enabled");

        _sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        await Fire(NodeDataTopic, AliasOnlyPayload(25.5));

        service.GetPoints(byName).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(25.5, 0.001);
    }

    [Test]
    public async Task Series_CollectsNoPoints_UntilTheServiceKnowsTheConnection()
    {
        await ApplyNodeBirthAsync();
        var connectionId = Guid.NewGuid();
        var seriesId = ConfigureSeries(connectionId, NodeDataTopic, $"metrics.{MetricName}");

        using var service = CreateService();
        await service.StartAsync();

        await Fire(NodeDataTopic, AliasOnlyPayload(24.5));

        service.GetPoints(seriesId).Should().BeEmpty(
            "series are collected per connection, and only the Charts page tells the service the connection");
    }
}
