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

        _chartSettings = Substitute.For<IChartSettings>();
        _chartSettings.GetCharts(_connectionId).Returns([]);
        _chartSettings.AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        Services.AddChartSettings(_chartSettings);

        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        EnsureMudProviders();
    }

    [Test]
    public async Task NodeMetricChartButton_PersistsChartWithNodeTopicAndMetricPath()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("CPU Usage (%)", "double", "42.5000", DateTime.UtcNow)];
        group.Nodes["edge-01"] = node;
        _topology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = Render<SparkplugNodesView>();
        var provider = Render<MudDialogProvider>();

        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        var click = cut.Find(ChartButtonSelector).ClickAsync(new());
        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Add field to chart"));
        provider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();
        provider.WaitForAssertion(() =>
            _chartSettings.Received(1).AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()));

        await click.WaitAsync(TimeSpan.FromSeconds(10));

        await _chartSettings.Received(1).AddChartAsync(_connectionId, Arg.Is<ChartConfiguration>(c =>
            c.Name == "factory edge-01"
            && c.Series.Count == 1
            && c.Series[0].Topic == "spBv1.0/factory/NDATA/edge-01"
            && c.Series[0].JsonPath == "metrics.CPU Usage (%)"
            && c.Series[0].DisplayName == "CPU Usage (%)"));
    }
}
