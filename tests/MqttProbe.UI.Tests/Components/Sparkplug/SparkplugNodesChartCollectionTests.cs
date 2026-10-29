using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesChartCollectionTests : BunitTestContext
{
    private const string ChartButtonSelector = "button[title='Add to chart']";
    private static readonly Guid _connectionId = Guid.NewGuid();

    private SparkplugTopologyService _topology = null!;
    private ChartSettings _chartSettings = null!;
    private FakeSettingsDocument _document = null!;
    private IMqttManagedClient _client = null!;
    private ChartDataService _chartData = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;
    private int _notifications;

    [SetUp]
    public async Task Setup()
    {
        _topology = new SparkplugTopologyService();
        await _topology.ApplyTopologyEventsAsync(
        [
            new NodeBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/NBIRTH/edge-01",
                GroupId = "factory",
                NodeId = "edge-01",
                Metrics = [new MetricSnapshot { Name = "Flow Rate (L/min)", DataType = "double", Value = "12.5", Alias = 42 }]
            },
            new DeviceBirthEvent
            {
                FormatId = "sparkplug-b",
                Topic = "spBv1.0/factory/DBIRTH/edge-01/sensor-A",
                GroupId = "factory",
                NodeId = "edge-01",
                DeviceId = "sensor-A",
                Metrics = [new MetricSnapshot { Name = "Return Temp (C)", DataType = "double", Value = "21.25", Alias = 7 }]
            }
        ]);

        Services.AddSingleton<ISparkplugTopologyService>(_topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });
        Services.AddSparkplugSettings(sparkplugSettings);

        var sessionState = Substitute.For<ISessionState>();
        sessionState.SelectedConnection.Returns(new Connection { Id = _connectionId });
        Services.AddSingleton(sessionState);

        _document = new FakeSettingsDocument();
        _chartSettings = new ChartSettings(_document);
        Services.AddChartSettings(_chartSettings);

        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        _client = Substitute.For<IMqttManagedClient>();
        _client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        _chartData = new ChartDataService(
            _client,
            new JsonFieldExtractor(),
            new ChartFieldRegistry(),
            _chartSettings,
            BuildPipeline(),
            sparkplugSettings,
            _topology);
        _chartData.SetConnection(_connectionId);
        _chartData.OnDataUpdated += () => _notifications++;
        // A live connection starts chart collection; this test never renders Charts.
        await _chartData.StartAsync();

        EnsureMudProviders();
    }

    [TearDown]
    public void TearDown()
    {
        _chartData.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task NodeMetricQuickAdd_CollectsAliasOnlyNodeUpdateBeforeChartsRendered()
    {
        var cut = RenderTopology(out var dialogProvider);

        var click = MetricRow(cut, "Flow Rate (L/min)").QuerySelector(ChartButtonSelector)!.ClickAsync(new());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Add field to chart"));
        dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();
        dialogProvider.WaitForAssertion(() => _chartSettings.GetCharts(_connectionId).Should().ContainSingle());
        await click;

        var series = _chartSettings.GetCharts(_connectionId).Single().Series.Should().ContainSingle().Subject;
        series.Topic.Should().Be("spBv1.0/factory/NDATA/edge-01");
        series.JsonPath.Should().Be("metrics.Flow Rate (L/min)");

        await FireAliasOnly("spBv1.0/factory/NDATA/edge-01",
            new Payload.Types.Metric { Alias = 42, DoubleValue = 26.5 });

        _chartData.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(26.5, 0.001);
    }

    [Test]
    public async Task DeviceMetricQuickAdd_CollectsAliasOnlyDeviceUpdateBeforeChartsRendered()
    {
        var cut = RenderTopology(out var dialogProvider);
        ExpandDevice(cut, "sensor-A");

        var click = MetricRow(cut, "Return Temp (C)").QuerySelector(ChartButtonSelector)!.ClickAsync(new());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Add field to chart"));
        dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();
        dialogProvider.WaitForAssertion(() => _chartSettings.GetCharts(_connectionId).Should().ContainSingle());
        await click;

        var series = _chartSettings.GetCharts(_connectionId).Single().Series.Should().ContainSingle().Subject;
        series.Topic.Should().Be("spBv1.0/factory/DDATA/edge-01/sensor-A");
        series.JsonPath.Should().Be("metrics.Return Temp (C)");

        await FireAliasOnly("spBv1.0/factory/DDATA/edge-01/sensor-A",
            new Payload.Types.Metric { Alias = 7, DoubleValue = 24.75 });

        _chartData.GetPoints(series.Id).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(24.75, 0.001);
    }

    [Test]
    public async Task CancelledQuickAdd_LeavesStoredChartsUnchangedAndCollectsNothing()
    {
        var existing = new ChartConfiguration
        {
            Name = "Existing Chart",
            Series =
            [
                new ChartSeries
                {
                    DisplayName = "Existing",
                    Topic = "spBv1.0/factory/NDATA/edge-01",
                    JsonPath = "metrics.Existing"
                }
            ]
        };
        _document.Config.ChartsByConnection[_connectionId] = [existing];

        var cut = RenderTopology(out var dialogProvider);

        var click = MetricRow(cut, "Flow Rate (L/min)").QuerySelector(ChartButtonSelector)!.ClickAsync(new());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Add field to chart"));
        dialogProvider.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
        await click;

        await FireAliasOnly("spBv1.0/factory/NDATA/edge-01",
            new Payload.Types.Metric { Alias = 42, DoubleValue = 26.5 });

        var stored = _chartSettings.GetCharts(_connectionId).Should().ContainSingle().Subject;
        stored.Id.Should().Be(existing.Id);
        stored.Series.Should().ContainSingle("cancelling adds no series");
        stored.Series[0].JsonPath.Should().Be("metrics.Existing");
        _chartData.GetPoints(stored.Series[0].Id).Should().BeEmpty();
        _notifications.Should().Be(0, "no chart exists for the cancelled field, so nothing receives the update");
    }

    private IRenderedComponent<SparkplugNodesView> RenderTopology(
        out IRenderedComponent<MudDialogProvider> dialogProvider)
    {
        dialogProvider = Render<MudDialogProvider>();
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(row => row.TextContent.Contains("edge-01")).Click();
        return cut;
    }

    private static void ExpandDevice(IRenderedComponent<SparkplugNodesView> cut, string deviceId) =>
        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains(deviceId)).Click();

    private static AngleSharp.Dom.IElement MetricRow(
        IRenderedComponent<SparkplugNodesView> cut, string metricName) =>
        cut.FindAll("tr.spb-metric-row").Single(row =>
            row.QuerySelector(".spb-td-name")?.TextContent.Trim() == metricName);

    private async Task FireAliasOnly(string topic, Payload.Types.Metric metric)
    {
        var payload = new Payload { Timestamp = 2 };
        payload.Metrics.Add(metric);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload.ToByteArray()).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        await _handler!(new MqttApplicationMessageReceivedEventArgs("test-client", message, packet, null));
    }

    private static PayloadPipeline BuildPipeline()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return new PayloadPipeline(builder.Build(), Substitute.For<ILogger<PayloadPipeline>>());
    }

    private sealed class FakeSettingsDocument : ISettingsDocument
    {
        public AppConfiguration Config { get; } = new();

        public Task MutateAndSaveAsync(Action<AppConfiguration> mutate)
        {
            mutate(Config);
            return Task.CompletedTask;
        }

        public Task ExclusiveAsync(Func<Task> action) => action();

        public Task SaveAsync() => Task.CompletedTask;
    }
}
