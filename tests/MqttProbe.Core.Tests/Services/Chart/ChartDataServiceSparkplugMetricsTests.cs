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
public class ChartDataServiceSparkplugMetricsTests
{
    private const string GroupId = "g";
    private const string NodeId = "eon1";
    private const string DeviceId = "dev1";
    private const string NodeDataTopic = "spBv1.0/g/NDATA/eon1";
    private const string DeviceDataTopic = "spBv1.0/g/DDATA/eon1/dev1";

    // Sparkplug B datatype codes carried in the metric metadata.
    private const uint DataTypeInt64 = 4;
    private const uint DataTypeDouble = 10;
    private const uint DataTypeBoolean = 11;

    [Test]
    public async Task NodeData_NamedDoubleMetric_ChartsSeriesWithMetricsPath()
    {
        var series = NewSeries(NodeDataTopic, "metrics.Flow Rate");
        using var harness = new ChartHarness(series: [series]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(DoubleMetric("Flow Rate", 24.5)));

        harness.Service.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task NodeData_NamedBooleanMetric_ChartsSeriesWithMetricsPath()
    {
        var running = NewSeries(NodeDataTopic, "metrics.Pump Running");
        var closed = NewSeries(NodeDataTopic, "metrics.Valve Open");
        using var harness = new ChartHarness(series: [running, closed]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(
            BooleanMetric("Pump Running", true),
            BooleanMetric("Valve Open", false)));

        harness.Service.GetPoints(running.Id).Should().ContainSingle()
            .Which.Value.Should().Be(1.0);
        harness.Service.GetPoints(closed.Id).Should().ContainSingle()
            .Which.Value.Should().Be(0.0);
    }

    [Test]
    public async Task NodeData_NamedInt64Metric_ChartsSeriesWithMetricsPath()
    {
        var series = NewSeries(NodeDataTopic, "metrics.Total Pulses");
        using var harness = new ChartHarness(series: [series]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(Int64Metric("Total Pulses", 42)));

        harness.Service.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(42.0, 0.001);
    }

    [Test]
    public async Task DeviceData_NamedDoubleMetric_ChartsSeriesWithMetricsPath()
    {
        var series = NewSeries(DeviceDataTopic, "metrics.Device Temp");
        using var harness = new ChartHarness(series: [series]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(DeviceDataTopic, PayloadOf(DoubleMetric("Device Temp", 36.75)));

        harness.Service.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(36.75, 0.001);
    }

    [Test]
    public async Task NodeData_AliasOnlyMetric_ChartsSeriesWithMetricsPath_WhenAliasEnrichmentEnabled()
    {
        var series = NewSeries(NodeDataTopic, "metrics.Flow Rate");
        using var harness = new ChartHarness(
            topology: await NodeTopologyAsync("Flow Rate", 42),
            series: [series]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(AliasOnlyDoubleMetric(42, 24.5)));

        harness.Service.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task DeviceData_AliasOnlyMetric_ChartsSeriesWithMetricsPath_WhenAliasEnrichmentEnabled()
    {
        var series = NewSeries(DeviceDataTopic, "metrics.Flow Rate");
        using var harness = new ChartHarness(
            topology: await DeviceTopologyAsync("Flow Rate", 42),
            series: [series]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(DeviceDataTopic, PayloadOf(AliasOnlyDoubleMetric(42, 24.5)));

        harness.Service.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    [Test]
    public async Task AliasOnlyMetric_ChartsNoPoints_WhenAliasEnrichmentDisabled()
    {
        var series = NewSeries(NodeDataTopic, "metrics.Flow Rate");
        using var harness = new ChartHarness(
            topology: await NodeTopologyAsync("Flow Rate", 42),
            enrichAliasNames: false,
            series: [series]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(AliasOnlyDoubleMetric(42, 24.5)));

        harness.Service.GetPoints(series.Id).Should().BeEmpty();
    }

    [Test]
    public async Task NodeData_NamedAndAliasOnlyMetrics_ChartBothSeries()
    {
        var named = NewSeries(NodeDataTopic, "metrics.Flow Rate");
        var aliased = NewSeries(NodeDataTopic, "metrics.Pump Running");
        using var harness = new ChartHarness(
            topology: await NodeTopologyAsync("Pump Running", 7),
            series: [named, aliased]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(
            DoubleMetric("Flow Rate", 24.5),
            AliasOnlyBooleanMetric(7, true)));

        harness.Service.GetPoints(named.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
        harness.Service.GetPoints(aliased.Id).Should().ContainSingle()
            .Which.Value.Should().Be(1.0);
    }

    [Test]
    public async Task SeriesPathWithDifferentCase_ChartsNoPoints()
    {
        var series = NewSeries(NodeDataTopic, "metrics.flow rate");
        using var harness = new ChartHarness(series: [series]);
        await harness.StartAsync();
        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(DoubleMetric("Flow Rate", 24.5)));

        harness.Service.GetPoints(series.Id).Should().BeEmpty();
    }

    [Test]
    public async Task ChartsForConnection_ReceivePointsOnlyAfterSetConnection()
    {
        var series = NewSeries(NodeDataTopic, "metrics.Flow Rate");
        using var harness = new ChartHarness(series: [series]);
        await harness.StartAsync();

        await harness.Fire(NodeDataTopic, PayloadOf(DoubleMetric("Flow Rate", 24.5)));

        harness.Service.GetPoints(series.Id).Should().BeEmpty(
            "the service binds charts to a connection through SetConnection, and nothing calls it before the Charts view opens");

        harness.Service.SetConnection(harness.ConnectionId);

        await harness.Fire(NodeDataTopic, PayloadOf(DoubleMetric("Flow Rate", 24.5)));

        harness.Service.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.5, 0.001);
    }

    private static ChartSeries NewSeries(string topic, string jsonPath) =>
        new() { Id = Guid.NewGuid(), Topic = topic, JsonPath = jsonPath };

    private static byte[] PayloadOf(params Payload.Types.Metric[] metrics)
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.AddRange(metrics);
        return payload.ToByteArray();
    }

    private static Payload.Types.Metric DoubleMetric(string name, double value) =>
        new() { Name = name, Datatype = DataTypeDouble, DoubleValue = value };

    private static Payload.Types.Metric BooleanMetric(string name, bool value) =>
        new() { Name = name, Datatype = DataTypeBoolean, BooleanValue = value };

    private static Payload.Types.Metric Int64Metric(string name, ulong value) =>
        new() { Name = name, Datatype = DataTypeInt64, LongValue = value };

    private static Payload.Types.Metric AliasOnlyDoubleMetric(ulong alias, double value) =>
        new() { Alias = alias, Datatype = DataTypeDouble, DoubleValue = value };

    private static Payload.Types.Metric AliasOnlyBooleanMetric(ulong alias, bool value) =>
        new() { Alias = alias, Datatype = DataTypeBoolean, BooleanValue = value };

    private static Task<SparkplugTopologyService> NodeTopologyAsync(string metricName, ulong alias) =>
        ApplyBirthAsync(new NodeBirthEvent
        {
            FormatId = "sparkplug-b",
            Topic = $"spBv1.0/{GroupId}/NBIRTH/{NodeId}",
            GroupId = GroupId,
            NodeId = NodeId,
            Metrics = [new MetricSnapshot { Name = metricName, DataType = "double", Value = "1.0", Alias = alias }]
        });

    private static Task<SparkplugTopologyService> DeviceTopologyAsync(string metricName, ulong alias) =>
        ApplyBirthAsync(new DeviceBirthEvent
        {
            FormatId = "sparkplug-b",
            Topic = $"spBv1.0/{GroupId}/DBIRTH/{NodeId}/{DeviceId}",
            GroupId = GroupId,
            NodeId = NodeId,
            DeviceId = DeviceId,
            Metrics = [new MetricSnapshot { Name = metricName, DataType = "double", Value = "1.0", Alias = alias }]
        });

    private static async Task<SparkplugTopologyService> ApplyBirthAsync(TopologyEvent birthEvent)
    {
        var topology = new SparkplugTopologyService();
        await topology.ApplyTopologyEventsAsync([birthEvent]);
        return topology;
    }

    private sealed class ChartHarness : IDisposable
    {
        private readonly IMqttManagedClient _client;
        private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

        public Guid ConnectionId { get; } = Guid.NewGuid();

        public ChartDataService Service { get; }

        public ChartHarness(
            ISparkplugTopologyService? topology = null,
            bool enrichAliasNames = true,
            params ChartSeries[] series)
        {
            _client = Substitute.For<IMqttManagedClient>();
            _client
                .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
                .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

            var chartSettings = new ConnectionScopedChartSettings(ConnectionId, series);
            var sparkplugSettings = Substitute.For<ISparkplugSettings>();
            sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = enrichAliasNames });

            Service = new ChartDataService(
                _client,
                new JsonFieldExtractor(),
                new ChartFieldRegistry(),
                chartSettings,
                TestPipelineHelper.BuildBuiltInPipeline(),
                sparkplugSettings,
                topology);
        }

        public Task StartAsync() => Service.StartAsync();

        public Task Fire(string topic, byte[] payload) =>
            _handler!(MakeArgs(topic, payload));

        public void Dispose()
        {
            Service.Dispose();
            _client.Dispose();
        }

        private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, byte[] payload)
        {
            var appMsg = new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(payload).Build();
            var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
            return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
        }
    }

    private sealed class ConnectionScopedChartSettings : IChartSettings
    {
        private readonly Guid _connectionId;
        private readonly ChartConfiguration _configuration;

        public ConnectionScopedChartSettings(Guid connectionId, ChartSeries[] series)
        {
            _connectionId = connectionId;
            _configuration = new ChartConfiguration { MaxPoints = 100, Series = [.. series] };
        }

        public event Action<Guid>? ChartsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ChartConfiguration> GetCharts(Guid connectionId) =>
            connectionId == _connectionId ? [_configuration] : [];

        public Task AddChartAsync(Guid connectionId, ChartConfiguration chart) => Task.CompletedTask;

        public Task UpdateChartAsync(Guid connectionId, ChartConfiguration chart) => Task.CompletedTask;

        public Task RemoveChartAsync(Guid connectionId, Guid chartId) => Task.CompletedTask;
    }
}
