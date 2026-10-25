using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesViewAddToChartTests : BunitTestContext
{
    private const string AddToChartSelector = "button[title='Add to chart']";
    private ISparkplugTopologyService _topology = null!;
    private IDialogService _dialogService = null!;
    private IDialogReference _dialogRef = null!;
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

        _dialogRef = Substitute.For<IDialogReference>();
        _dialogService = Substitute.For<IDialogService>();
        Services.AddSingleton(_dialogService);
        _snackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_snackbar);
        EnsureMudProviders();
    }

    [Test]
    public void NodeMetricRow_ShowsAddToChartButton_ForChartableMetric()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow)];

        var cut = RenderNode(node);

        MetricRow(cut, "Flow Rate").QuerySelector(AddToChartSelector)
            .Should().NotBeNull();
    }

    [Test]
    public void NodeMetricChartButton_UsesShortVisibleLabelButKeepsDescriptiveName()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow)];

        var cut = RenderNode(node);

        var button = MetricRow(cut, "Flow Rate").QuerySelector(AddToChartSelector)!;
        button.QuerySelector(".json-chart-btn__label")!.TextContent.Should().Be("Chart");
        button.GetAttribute("title").Should().Be("Add to chart");
        button.GetAttribute("aria-label").Should().Be("Add to chart");
    }

    [Test]
    public void NodeMetricRows_FollowPayloadExtractionEligibility()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics =
        [
            new SpbMetricSnapshot("Numeric", "double", "12.5000", DateTime.UtcNow),
            new SpbMetricSnapshot("Switch", "bool", "true", DateTime.UtcNow),
            new SpbMetricSnapshot("Numeric String", "string", "42", DateTime.UtcNow),
            new SpbMetricSnapshot("Status", "string", "running", DateTime.UtcNow),
            new SpbMetricSnapshot("Missing", "unknown", "\u2014", DateTime.UtcNow)
        ];

        var cut = RenderNode(node);

        MetricRow(cut, "Numeric").QuerySelector(AddToChartSelector).Should().NotBeNull();
        MetricRow(cut, "Switch").QuerySelector(AddToChartSelector).Should().NotBeNull();
        MetricRow(cut, "Numeric String").QuerySelector(AddToChartSelector).Should().BeNull();
        MetricRow(cut, "Status").QuerySelector(AddToChartSelector).Should().BeNull();
        MetricRow(cut, "Missing").QuerySelector(AddToChartSelector).Should().BeNull();
    }

    [Test]
    public void ExpandedDeviceMetricRows_ExcludeStringValueMetrics()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        var device = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };
        device.Metrics =
        [
            new SpbMetricSnapshot("Native Reading", "double", "21.2500", DateTime.UtcNow),
            new SpbMetricSnapshot("String Number", "string", "42", DateTime.UtcNow),
            new SpbMetricSnapshot("String Boolean", "string", "true", DateTime.UtcNow),
            new SpbMetricSnapshot("Native Switch", "bool", "false", DateTime.UtcNow)
        ];
        node.Devices[device.DeviceId] = device;

        var cut = RenderNode(node);
        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains("sensor-A")).Click();

        MetricRow(cut, "Native Reading").QuerySelector(AddToChartSelector).Should().NotBeNull();
        MetricRow(cut, "Native Switch").QuerySelector(AddToChartSelector).Should().NotBeNull();
        MetricRow(cut, "String Number").QuerySelector(AddToChartSelector).Should().BeNull();
        MetricRow(cut, "String Boolean").QuerySelector(AddToChartSelector).Should().BeNull();
    }

    [Test]
    public void NodeAndExpandedDevice_ShowAddToChart_ForDatetimeMetrics()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Node Timestamp", "datetime", "1712345678000", DateTime.UtcNow)];
        var device = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };
        device.Metrics = [new SpbMetricSnapshot("Device Timestamp", "datetime", "1712345678000", DateTime.UtcNow)];
        node.Devices[device.DeviceId] = device;

        var cut = RenderNode(node);
        MetricRow(cut, "Node Timestamp").QuerySelector(AddToChartSelector).Should().NotBeNull();

        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains("sensor-A")).Click();
        MetricRow(cut, "Device Timestamp").QuerySelector(AddToChartSelector).Should().NotBeNull();
    }

    [Test]
    public async Task NodeMetric_Click_OpensDialogWithOngoingNodeTopicAndExactName()
    {
        var captured = CaptureDialog();
        _dialogRef.Result.Returns(Task.FromResult<DialogResult?>(
            DialogResult.Ok("Chart created: factory edge-01")));

        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate (L/min)", "double", "12.5000", DateTime.UtcNow)];
        var cut = RenderNode(node);

        await cut.InvokeAsync(() => MetricRow(cut, "Flow Rate (L/min)").QuerySelector(AddToChartSelector)!.Click());

        captured.Title.Should().Be("Chart Field");
        captured.Parameters.Should().NotBeNull();
        captured.Parameters!.Get<string>("Topic").Should().Be("spBv1.0/factory/NDATA/edge-01");
        captured.Parameters.Get<string>("JsonPath").Should().Be("metrics.Flow Rate (L/min)");
        captured.Parameters.Get<string>("SeriesName").Should().Be("Flow Rate (L/min)");
        captured.Options.Should().NotBeNull();
        captured.Options!.MaxWidth.Should().Be(MaxWidth.Small);
        captured.Options.FullWidth.Should().BeTrue();
        _snackbar.Received(1).Add("Chart created: factory edge-01", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task NodeMetric_Click_WhenDialogCanceled_ShowsNoSnackbar()
    {
        CaptureDialog();
        _dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));

        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("Flow Rate", "double", "12.5000", DateTime.UtcNow)];
        var cut = RenderNode(node);

        await cut.InvokeAsync(() => MetricRow(cut, "Flow Rate").QuerySelector(AddToChartSelector)!.Click());

        _snackbar.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<Severity>(),
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task DeviceMetric_Click_UsesOngoingDeviceTopicAndExactName()
    {
        var captured = CaptureDialog();
        _dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));

        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        var device = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };
        device.Metrics = [new SpbMetricSnapshot("Return Temperature", "double", "21.2500", DateTime.UtcNow)];
        node.Devices[device.DeviceId] = device;

        var cut = RenderNode(node);
        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains("sensor-A")).Click();

        await cut.InvokeAsync(() => MetricRow(cut, "Return Temperature").QuerySelector(AddToChartSelector)!.Click());

        captured.Parameters.Should().NotBeNull();
        captured.Parameters!.Get<string>("Topic").Should().Be("spBv1.0/factory/DDATA/edge-01/sensor-A");
        captured.Parameters.Get<string>("JsonPath").Should().Be("metrics.Return Temperature");
        captured.Parameters.Get<string>("SeriesName").Should().Be("Return Temperature");
    }

    private CapturedDialog CaptureDialog()
    {
        var captured = new CapturedDialog();
        _dialogService.ShowAsync<QuickAddToChartDialog>(
                Arg.Do<string?>(value => captured.Title = value),
                Arg.Do<DialogParameters>(value => captured.Parameters = value),
                Arg.Do<DialogOptions>(value => captured.Options = value))
            .Returns(_dialogRef);
        return captured;
    }

    private IRenderedComponent<SparkplugNodesView> RenderNode(SpbNode node)
    {
        var group = new SpbGroup();
        group.Nodes[node.NodeId] = node;
        _topology.Groups.Returns(new Dictionary<string, SpbGroup> { [node.GroupId] = group });

        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(row => row.TextContent.Contains(node.NodeId)).Click();
        return cut;
    }

    private static AngleSharp.Dom.IElement MetricRow(
        IRenderedComponent<SparkplugNodesView> cut, string metricName) =>
        cut.FindAll("tr.spb-metric-row").Single(row =>
            row.QuerySelector(".spb-td-name")?.TextContent.Trim() == metricName);

    private sealed class CapturedDialog
    {
        public string? Title { get; set; }
        public DialogParameters? Parameters { get; set; }
        public DialogOptions? Options { get; set; }
    }
}
