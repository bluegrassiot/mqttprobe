using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesViewChartPersistenceTests : BunitTestContext
{
    private const string ChartButtonSelector = "button[title='Add to chart']";
    private static readonly Guid _connectionId = Guid.NewGuid();

    private ISparkplugTopologyService _topology = null!;
    private IChartSettings _chartSettings = null!;
    private FakeSettingsDocument _document = null!;
    private ISnackbar _snackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _topology = Substitute.For<ISparkplugTopologyService>();
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(sparkplugSettings);

        var sessionState = Substitute.For<ISessionState>();
        sessionState.SelectedConnection.Returns(new Connection { Id = _connectionId });
        Services.AddSingleton(sessionState);

        _document = new FakeSettingsDocument();
        _chartSettings = new ChartSettings(_document);
        Services.AddChartSettings(_chartSettings);

        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        _snackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_snackbar);

        EnsureMudProviders();
    }

    [Test]
    public async Task NodeMetric_QuickAddCreateNew_PersistsChartAndSeriesInStoredState()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate (L/min)", "double", "12.5000", DateTime.UtcNow)];
        var cut = RenderNode(node, out var dialogProvider);

        var click = MetricRow(cut, "Flow Rate (L/min)").QuerySelector(ChartButtonSelector)!.ClickAsync(new());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Add field to chart"));
        dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();
        dialogProvider.WaitForAssertion(() => _chartSettings.GetCharts(_connectionId).Should().ContainSingle());
        await click;

        var chart = _chartSettings.GetCharts(_connectionId).Should().ContainSingle().Subject;
        chart.Name.Should().Be("factory edge-01");
        var series = chart.Series.Should().ContainSingle().Subject;
        series.Topic.Should().Be("spBv1.0/factory/NDATA/edge-01");
        series.JsonPath.Should().Be("metrics.Flow Rate (L/min)");
        series.DisplayName.Should().Be("Flow Rate (L/min)");

        _snackbar.Received(1).Add("Chart created: factory edge-01", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task DeviceMetric_QuickAddCreateNew_PersistsChartWithDeviceTopic()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        var device = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };
        device.Metrics = [new SpbMetricSnapshot("Return Temp (C)", "double", "21.2500", DateTime.UtcNow)];
        node.Devices[device.DeviceId] = device;
        var cut = RenderNode(node, out var dialogProvider);
        ExpandDevice(cut, "sensor-A");

        var click = MetricRow(cut, "Return Temp (C)").QuerySelector(ChartButtonSelector)!.ClickAsync(new());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Add field to chart"));
        dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();
        dialogProvider.WaitForAssertion(() => _chartSettings.GetCharts(_connectionId).Should().ContainSingle());
        await click;

        var chart = _chartSettings.GetCharts(_connectionId).Should().ContainSingle().Subject;
        chart.Name.Should().Be("factory edge-01 sensor-A");
        var series = chart.Series.Should().ContainSingle().Subject;
        series.Topic.Should().Be("spBv1.0/factory/DDATA/edge-01/sensor-A");
        series.JsonPath.Should().Be("metrics.Return Temp (C)");
        series.DisplayName.Should().Be("Return Temp (C)");

        _snackbar.Received(1).Add("Chart created: factory edge-01 sensor-A", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task QuickAddAddToExisting_AppendsSeriesToStoredChartWithoutMutatingOriginal()
    {
        var existing = SeedExistingChart();

        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow)];
        var cut = RenderNode(node, out var dialogProvider);

        var click = MetricRow(cut, "Flow Rate").QuerySelector(ChartButtonSelector)!.ClickAsync(new());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Add field to chart"));
        dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();
        dialogProvider.WaitForAssertion(() =>
            _chartSettings.GetCharts(_connectionId).Should().ContainSingle().Which.Series.Should().HaveCount(2));
        await click;

        var stored = _chartSettings.GetCharts(_connectionId).Should().ContainSingle().Subject;
        stored.Id.Should().Be(existing.Id);
        stored.Name.Should().Be("Existing Chart");
        stored.Series[0].JsonPath.Should().Be("metrics.Existing");
        stored.Series[1].Topic.Should().Be("spBv1.0/factory/NDATA/edge-01");
        stored.Series[1].JsonPath.Should().Be("metrics.Flow Rate");
        stored.Series[1].DisplayName.Should().Be("Flow Rate");
        existing.Series.Should().ContainSingle("the live chart must not be mutated before persistence");

        _snackbar.Received(1).Add("Series added to chart: Existing Chart", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Cancel_LeavesStoredChartsUnchanged()
    {
        var existing = SeedExistingChart();

        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow)];
        var cut = RenderNode(node, out var dialogProvider);

        var click = MetricRow(cut, "Flow Rate").QuerySelector(ChartButtonSelector)!.ClickAsync(new());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Add field to chart"));
        dialogProvider.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
        await click;

        var stored = _chartSettings.GetCharts(_connectionId).Should().ContainSingle().Subject;
        stored.Id.Should().Be(existing.Id);
        stored.Name.Should().Be("Existing Chart");
        stored.Series.Should().ContainSingle("cancelling adds no series");
        stored.Series[0].JsonPath.Should().Be("metrics.Existing");
        _snackbar.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<Severity>(),
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    private ChartConfiguration SeedExistingChart()
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
        return existing;
    }

    private IRenderedComponent<SparkplugNodesView> RenderNode(
        SpbNode node, out IRenderedComponent<MudDialogProvider> dialogProvider)
    {
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _topology.Groups.Returns(new Dictionary<string, SpbGroup> { [node.GroupId] = group });

        dialogProvider = Render<MudDialogProvider>();
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(row => row.TextContent.Contains(node.NodeId)).Click();
        return cut;
    }

    private static void ExpandDevice(IRenderedComponent<SparkplugNodesView> cut, string deviceId) =>
        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains(deviceId)).Click();

    private static AngleSharp.Dom.IElement MetricRow(
        IRenderedComponent<SparkplugNodesView> cut, string metricName) =>
        cut.FindAll("tr.spb-metric-row").Single(row =>
            row.QuerySelector(".spb-td-name")?.TextContent.Trim() == metricName);

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
