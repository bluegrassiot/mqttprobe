using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugMetricsChartTests : BunitTestContext
{
    private const string ChartButtonSelector = "button[title='Add to chart']";

    private ISparkplugTopologyService _mockTopology = null!;
    private ISparkplugCommandService _mockCommandService = null!;
    private ISparkplugSettings _mockSparkplugSettings = null!;
    private IDialogService _mockDialogService = null!;
    private ISnackbar _mockSnackbar = null!;
    private DialogParameters? _capturedParameters;

    [SetUp]
    public void SetupMocks()
    {
        _mockTopology = Substitute.For<ISparkplugTopologyService>();
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_mockTopology);

        _mockCommandService = Substitute.For<ISparkplugCommandService>();
        Services.AddSingleton(_mockCommandService);

        _mockSparkplugSettings = Substitute.For<ISparkplugSettings>();
        _mockSparkplugSettings.Sparkplug.Returns(new AppConfiguration().Sparkplug ?? new SparkplugSettings());
        Services.AddSparkplugSettings(_mockSparkplugSettings);

        _mockDialogService = Substitute.For<IDialogService>();
        Services.AddSingleton(_mockDialogService);

        _mockSnackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_mockSnackbar);

        _capturedParameters = null;
        EnsureMudProviders();
    }

    private void SetupDialog(DialogResult? result)
    {
        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(Task.FromResult(result));
        _mockDialogService
            .ShowAsync<QuickAddToChartDialog>(
                Arg.Any<string>(),
                Arg.Do<DialogParameters>(p => _capturedParameters = p),
                Arg.Any<DialogOptions>())
            .Returns(Task.FromResult(reference));
    }

    private static SpbGroup GroupWithNode(params SpbMetricSnapshot[] metrics)
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = metrics;
        group.Nodes["edge-01"] = node;
        return group;
    }

    private IRenderedComponent<SparkplugNodesView> RenderNode(IRenderedComponent<SparkplugNodesView> cut)
    {
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        return cut;
    }

    [Test]
    public void MetricRows_ShowNoChartButton_BeforeANodeIsSelected()
    {
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["factory"] = GroupWithNode(new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow))
        });

        var cut = Render<SparkplugNodesView>();

        cut.FindAll(ChartButtonSelector).Should().BeEmpty();
    }

    [Test]
    public void NodeMetricRow_ShowsChartButton()
    {
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["factory"] = GroupWithNode(new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow))
        });

        var cut = RenderNode(Render<SparkplugNodesView>());

        cut.FindAll(ChartButtonSelector).Should().ContainSingle();
    }

    [Test]
    public void NodeMetricRow_ShowsNoChartButton_ForNonNumericMetric()
    {
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["factory"] = GroupWithNode(new SpbMetricSnapshot("Status", "string", "running", DateTime.UtcNow))
        });

        var cut = RenderNode(Render<SparkplugNodesView>());

        cut.FindAll(ChartButtonSelector).Should().BeEmpty();
    }

    [Test]
    public void NodeMetricRow_ShowsNoChartButton_ForNumericLookingStringMetric()
    {
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["factory"] = GroupWithNode(new SpbMetricSnapshot("Counter", "string", "42", DateTime.UtcNow))
        });

        var cut = RenderNode(Render<SparkplugNodesView>());

        cut.FindAll(ChartButtonSelector).Should().BeEmpty(
            "a Sparkplug string metric is not collected, so charting it would create an empty series");
    }

    [Test]
    public async Task NodeMetricChartButton_PassesNodeDataTopicPathAndSeriesNameToDialog()
    {
        SetupDialog(DialogResult.Cancel());
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["factory"] = GroupWithNode(new SpbMetricSnapshot("Motor Speed", "double", "20.0000", DateTime.UtcNow))
        });

        var cut = RenderNode(Render<SparkplugNodesView>());

        await cut.InvokeAsync(() => cut.Find(ChartButtonSelector).Click());

        _capturedParameters.Should().NotBeNull();
        var typed = (DialogParameters<QuickAddToChartDialog>)_capturedParameters!;
        typed.Get(x => x.Topic).Should().Be("spBv1.0/factory/NDATA/edge-01");
        typed.Get(x => x.JsonPath).Should().Be("metrics.Motor Speed");
        typed.Get(x => x.SeriesName).Should().Be("Motor Speed");
    }

    [Test]
    public async Task NodeMetricChartButton_WhenDialogSucceeds_ShowsSuccessSnackbar()
    {
        SetupDialog(DialogResult.Ok("Chart created: factory edge-01"));
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["factory"] = GroupWithNode(new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow))
        });

        var cut = RenderNode(Render<SparkplugNodesView>());

        await cut.InvokeAsync(() => cut.Find(ChartButtonSelector).Click());

        _mockSnackbar.Received(1).Add(
            "Chart created: factory edge-01", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task NodeMetricChartButton_WhenDialogIsCancelled_ShowsNoSnackbar()
    {
        SetupDialog(DialogResult.Cancel());
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>
        {
            ["factory"] = GroupWithNode(new SpbMetricSnapshot("Temp", "double", "20.0000", DateTime.UtcNow))
        });

        var cut = RenderNode(Render<SparkplugNodesView>());

        await cut.InvokeAsync(() => cut.Find(ChartButtonSelector).Click());

        _mockSnackbar.DidNotReceive().Add(
            Arg.Any<string>(), Arg.Any<Severity>(), Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void DeviceMetricRow_ShowsChartButtonOnlyWhenTheDeviceIsExpanded()
    {
        var group = GroupWithNode();
        var device = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };
        device.Metrics = [new SpbMetricSnapshot("Voltage", "double", "220.0000", DateTime.UtcNow)];
        group.Nodes["edge-01"].Devices["sensor-A"] = device;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = RenderNode(Render<SparkplugNodesView>());
        cut.FindAll(ChartButtonSelector).Should().BeEmpty("the device metrics are collapsed");

        cut.FindAll(".spb-device-row").First(r => r.TextContent.Contains("sensor-A")).Click();

        cut.FindAll(ChartButtonSelector).Should().ContainSingle();
    }

    [Test]
    public async Task DeviceMetricChartButton_PassesDeviceDataTopicPathAndSeriesNameToDialog()
    {
        SetupDialog(DialogResult.Cancel());
        var group = GroupWithNode();
        var device = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };
        device.Metrics = [new SpbMetricSnapshot("Supply Voltage", "double", "220.0000", DateTime.UtcNow)];
        group.Nodes["edge-01"].Devices["sensor-A"] = device;
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });

        var cut = RenderNode(Render<SparkplugNodesView>());
        cut.FindAll(".spb-device-row").First(r => r.TextContent.Contains("sensor-A")).Click();

        await cut.InvokeAsync(() => cut.Find(ChartButtonSelector).Click());

        _capturedParameters.Should().NotBeNull();
        var typed = (DialogParameters<QuickAddToChartDialog>)_capturedParameters!;
        typed.Get(x => x.Topic).Should().Be("spBv1.0/factory/DDATA/edge-01/sensor-A");
        typed.Get(x => x.JsonPath).Should().Be("metrics.Supply Voltage");
        typed.Get(x => x.SeriesName).Should().Be("Supply Voltage");
    }
}
