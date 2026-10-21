using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugMetricChartTargetTests
{
    [Test]
    public void MetricChartPath_UsesMetricsDotNameConvention()
    {
        SparkplugNodeDisplay.MetricChartPath("CPU Usage (%)").Should().Be("metrics.CPU Usage (%)");
    }

    [Test]
    public void MetricChartPath_PreservesCaseAndSpaces()
    {
        SparkplugNodeDisplay.MetricChartPath("Working Set (MB)").Should().Be("metrics.Working Set (MB)");
    }

    [Test]
    public void NodeDataTopic_UsesNdataVerb()
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory" };

        SparkplugNodeDisplay.NodeDataTopic(node).Should().Be("spBv1.0/factory/NDATA/edge-01");
    }

    [Test]
    public void DeviceDataTopic_UsesDdataVerbAndDeviceSegment()
    {
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = "edge-01", GroupId = "factory" };

        SparkplugNodeDisplay.DeviceDataTopic(device).Should().Be("spBv1.0/factory/DDATA/edge-01/sensor-A");
    }
}

[TestFixture]
public class SparkplugMetricChartPathConventionTests
{
    private const string NamedNodePayload =
        """{"timestamp":1700000000,"metrics":[{"name":"CPU Usage (%)","value":42.5}]}""";

    private const string AliasOnlyPayload =
        """{"timestamp":1700000000,"metrics":[{"alias":"7","int_value":5}]}""";

    private readonly JsonFieldExtractor _extractor = new();

    [Test]
    public void NodeMetricPath_MatchesExtractorKey_ForNamedMetric()
    {
        var fields = _extractor.Extract(NamedNodePayload);

        fields.Keys.Should().Contain(SparkplugNodeDisplay.MetricChartPath("CPU Usage (%)"));
    }

    [Test]
    public void DeviceMetricPath_MatchesExtractorKey_ForNamedMetric()
    {
        var fields = _extractor.Extract(NamedNodePayload);

        fields.Keys.Should().Contain(SparkplugNodeDisplay.MetricChartPath("CPU Usage (%)"));
    }

    [Test]
    public void AliasOnlyMetricPath_MatchesExtractorKey_WhenAliasEnrichmentResolvesTheName()
    {
        var aliasNames = new Dictionary<ulong, string> { [7UL] = "Motor Speed" };

        var fields = _extractor.Extract(AliasOnlyPayload, aliasNames);

        fields.Keys.Should().Contain(SparkplugNodeDisplay.MetricChartPath("Motor Speed"));
    }

    [Test]
    public void AliasOnlyMetricPath_IsNotExtracted_WithoutAliasEnrichment()
    {
        var fields = _extractor.Extract(AliasOnlyPayload);

        fields.Keys.Should().NotContain(SparkplugNodeDisplay.MetricChartPath("Motor Speed"));
        fields.Keys.Should().Contain("metrics[0].int_value");
    }

    [Test]
    public void StringMetricPath_YieldsNoChartableField()
    {
        var fields = _extractor.Extract("""{"metrics":[{"name":"Serial","string_value":"42"}]}""");

        // A named metric claims the array for the named keys, and a string value is
        // not one of the metric value fields the extractor reads, so nothing is left.
        fields.Should().BeEmpty("a named string metric carries no value the chart extractor reads");
    }

    [Test]
    public void DateTimeMetricPath_CarriesTheRawEpochNumber()
    {
        var fields = _extractor.Extract("""{"metrics":[{"name":"Started At","long_value":1700000000000}]}""");

        fields[SparkplugNodeDisplay.MetricChartPath("Started At")].Value.Should().Be(1700000000000d);
    }

    [TestCase("""{"timestamp":1,"metrics":[{"name":"Metric","int_value":3}]}""", true)]
    [TestCase("""{"timestamp":1,"metrics":[{"name":"Metric","double_value":42.5}]}""", true)]
    [TestCase("""{"timestamp":1,"metrics":[{"name":"Metric","long_value":1700000000}]}""", true)]
    [TestCase("""{"timestamp":1,"metrics":[{"name":"Metric","boolean_value":true}]}""", true)]
    [TestCase("""{"timestamp":1,"metrics":[{"name":"Metric","string_value":"running"}]}""", false)]
    [TestCase("""{"timestamp":1,"metrics":[{"name":"Metric","bytes_value":"AAEC"}]}""", false)]
    public void MetricPath_IsExtracted_OnlyForWireValuesTheChartReads(string payload, bool expected)
    {
        var fields = _extractor.Extract(payload);

        fields.Keys.Contains(SparkplugNodeDisplay.MetricChartPath("Metric")).Should().Be(expected);
    }
}

[TestFixture]
public class SparkplugNodeMetricChartTests : BunitTestContext
{
    private const string ChartButtonSelector = "button[title='Add to chart']";

    private static SpbNode NodeWith(params SpbMetricSnapshot[] metrics)
    {
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = metrics;
        return node;
    }

    [Test]
    public void NodeMetricRow_OffersChartButton_ForNumericMetric()
    {
        var node = NodeWith(new SpbMetricSnapshot("CPU Usage (%)", "double", "42.5000", DateTime.UtcNow));

        var cut = Render<SparkplugNodeDetail>(p => p.Add(x => x.Node, node));

        cut.FindAll(ChartButtonSelector).Should().ContainSingle();
    }

    [Test]
    public void NodeMetricRow_Click_RaisesRequestWithNodeTopicAndMetricPath()
    {
        var node = NodeWith(new SpbMetricSnapshot("CPU Usage (%)", "double", "42.5000", DateTime.UtcNow));
        SparkplugChartRequest? captured = null;

        var cut = Render<SparkplugNodeDetail>(p => p
            .Add(x => x.Node, node)
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<SparkplugChartRequest>(this, r => captured = r)));

        cut.Find(ChartButtonSelector).Click();

        captured.Should().NotBeNull();
        captured!.Topic.Should().Be("spBv1.0/factory/NDATA/edge-01");
        captured.JsonPath.Should().Be("metrics.CPU Usage (%)");
        captured.SeriesName.Should().Be("CPU Usage (%)");
    }

    [Test]
    public void NodeMetricRow_HasNoChartButton_ForNonNumericMetric()
    {
        var node = NodeWith(new SpbMetricSnapshot("Status", "string", "running", DateTime.UtcNow));

        var cut = Render<SparkplugNodeDetail>(p => p.Add(x => x.Node, node));

        cut.FindAll(ChartButtonSelector).Should().BeEmpty();
    }

    [Test]
    public void NodeMetricRow_OffersChartButton_ForBooleanMetric()
    {
        var node = NodeWith(new SpbMetricSnapshot("Node Control/Rebirth", "bool", "false", DateTime.UtcNow));

        var cut = Render<SparkplugNodeDetail>(p => p.Add(x => x.Node, node));

        cut.FindAll(ChartButtonSelector).Should().ContainSingle();
    }

    [Test]
    public void NodeMetricRow_OffersChartButton_OnlyForChartableTypes()
    {
        var node = NodeWith(
            new SpbMetricSnapshot("CPU Usage (%)", "double", "42.5000", DateTime.UtcNow),
            new SpbMetricSnapshot("Thread Count", "int32", "3", DateTime.UtcNow),
            new SpbMetricSnapshot("Node Control/Rebirth", "bool", "true", DateTime.UtcNow),
            new SpbMetricSnapshot("Started At", "datetime", "1700000000000", DateTime.UtcNow),
            new SpbMetricSnapshot("Status", "string", "running", DateTime.UtcNow),
            new SpbMetricSnapshot("Firmware Blob", "bytes", "AAEC", DateTime.UtcNow),
            new SpbMetricSnapshot("Missing Value", "double", "\u2014", DateTime.UtcNow));

        var cut = Render<SparkplugNodeDetail>(p => p.Add(x => x.Node, node));

        var chartable = cut.FindAll(".spb-metric-row")
            .Where(r => r.QuerySelector(ChartButtonSelector) is not null)
            .Select(r => r.QuerySelector(".spb-td-name")!.TextContent!.Trim())
            .ToList();

        chartable.Should().BeEquivalentTo(
            ["CPU Usage (%)", "Thread Count", "Node Control/Rebirth", "Started At"]);
    }

    [Test]
    public void DeviceMetricRow_Click_RaisesRequestWithDeviceTopicAndMetricPath()
    {
        var node = NodeWith(new SpbMetricSnapshot("CPU Usage (%)", "double", "42.5000", DateTime.UtcNow));
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        device.Metrics = [new SpbMetricSnapshot("Voltage", "double", "220.0000", DateTime.UtcNow)];
        node.Devices["sensor-A"] = device;
        SparkplugChartRequest? captured = null;

        var cut = Render<SparkplugNodeDetail>(p => p
            .Add(x => x.Node, node)
            .Add(x => x.ExpandedDevice, device)
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<SparkplugChartRequest>(this, r => captured = r)));

        cut.FindAll(ChartButtonSelector).Should().HaveCount(2);
        cut.FindAll(ChartButtonSelector)[1].Click();

        captured.Should().NotBeNull();
        captured!.Topic.Should().Be("spBv1.0/factory/DDATA/edge-01/sensor-A");
        captured.JsonPath.Should().Be("metrics.Voltage");
        captured.SeriesName.Should().Be("Voltage");
    }

    [Test]
    public void DeviceMetricRow_HasNoChartButton_ForStringMetric()
    {
        var node = NodeWith(new SpbMetricSnapshot("CPU Usage (%)", "double", "42.5000", DateTime.UtcNow));
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        device.Metrics = [new SpbMetricSnapshot("Serial Number", "string", "SN-0001", DateTime.UtcNow)];
        node.Devices["sensor-A"] = device;

        var cut = Render<SparkplugNodeDetail>(p => p
            .Add(x => x.Node, node)
            .Add(x => x.ExpandedDevice, device));

        cut.FindAll(".spb-device-metrics button[title='Add to chart']").Should().BeEmpty();
        cut.FindAll(ChartButtonSelector).Should().ContainSingle("only the node's numeric metric is chartable");
    }

    [Test]
    public void MessageDetails_OffersIndexedChartPath_ForNumericStringMetric()
    {
        ChartFieldSelection? captured = null;
        var cut = Render<JsonTreeView>(p => p
            .Add(x => x.Json, """{"metrics":[{"name":"Serial","string_value":"42"}]}""")
            .Add(x => x.OnAddToChart, EventCallback.Factory.Create<ChartFieldSelection>(this, s => captured = s)));

        cut.Instance.ExpandAll();
        cut.WaitForAssertion(() => cut.FindAll(".json-chart-btn").Should().ContainSingle());

        cut.Find(".json-chart-btn").Click();

        captured.Should().NotBeNull();
        captured!.JsonPath.Should().Be("metrics[0].string_value");
    }
}

[TestFixture]
public class SparkplugNodesViewChartTests : BunitTestContext
{
    private const string ChartButtonSelector = "button[title='Add to chart']";

    private ISparkplugTopologyService _topology = null!;
    private IDialogService _dialogService = null!;
    private IDialogReference _dialogReference = null!;
    private ISnackbar _snackbar = null!;
    private DialogParameters? _capturedParameters;

    [SetUp]
    public void SetupMocks()
    {
        _topology = Substitute.For<ISparkplugTopologyService>();
        _topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(_topology);

        var commandService = Substitute.For<ISparkplugCommandService>();
        Services.AddSingleton(commandService);

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(sparkplugSettings);

        _dialogReference = Substitute.For<IDialogReference>();
        _dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _dialogService = Substitute.For<IDialogService>();
        _dialogService
            .ShowAsync<QuickAddToChartDialog>(
                Arg.Any<string>(),
                Arg.Do<DialogParameters>(p => _capturedParameters = p),
                Arg.Any<DialogOptions>())
            .Returns(Task.FromResult(_dialogReference));
        Services.AddSingleton(_dialogService);

        _snackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_snackbar);

        EnsureMudProviders();
    }

    private void GivenNodeWithMetric()
    {
        var group = new SpbGroup();
        var node = new SpbNode { NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        node.Metrics = [new SpbMetricSnapshot("CPU Usage (%)", "double", "42.5000", DateTime.UtcNow)];
        var device = new SpbDevice { DeviceId = "sensor-A", NodeId = "edge-01", GroupId = "factory", Status = SpbNodeStatus.Online };
        device.Metrics = [new SpbMetricSnapshot("Voltage", "double", "220.0000", DateTime.UtcNow)];
        node.Devices["sensor-A"] = device;
        group.Nodes["edge-01"] = node;
        _topology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });
    }

    [Test]
    public async Task NodeMetricChartButton_OpensQuickAddDialogWithNodeTopicAndMetricPath()
    {
        GivenNodeWithMetric();
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        await cut.InvokeAsync(() => cut.Find(ChartButtonSelector).Click());

        _capturedParameters.Should().NotBeNull();
        var typed = (DialogParameters<QuickAddToChartDialog>)_capturedParameters!;
        typed.Get(x => x.Topic).Should().Be("spBv1.0/factory/NDATA/edge-01");
        typed.Get(x => x.JsonPath).Should().Be("metrics.CPU Usage (%)");
        typed.Get(x => x.SeriesName).Should().Be("CPU Usage (%)");
    }

    [Test]
    public async Task DeviceMetricChartButton_OpensQuickAddDialogWithDeviceTopicAndMetricPath()
    {
        GivenNodeWithMetric();
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();
        cut.FindAll(".spb-device-row").First(r => r.TextContent.Contains("sensor-A")).Click();

        await cut.InvokeAsync(() => cut.FindAll(ChartButtonSelector)[1].Click());

        _capturedParameters.Should().NotBeNull();
        var typed = (DialogParameters<QuickAddToChartDialog>)_capturedParameters!;
        typed.Get(x => x.Topic).Should().Be("spBv1.0/factory/DDATA/edge-01/sensor-A");
        typed.Get(x => x.JsonPath).Should().Be("metrics.Voltage");
        typed.Get(x => x.SeriesName).Should().Be("Voltage");
    }

    [Test]
    public async Task NodeMetricChartButton_WhenDialogConfirms_ShowsSuccessSnackbar()
    {
        _dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok("Chart created: factory edge-01")));
        GivenNodeWithMetric();
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        await cut.InvokeAsync(() => cut.Find(ChartButtonSelector).Click());

        _snackbar.Received(1).Add("Chart created: factory edge-01", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task NodeMetricChartButton_WhenDialogCancels_ShowsNoSnackbar()
    {
        GivenNodeWithMetric();
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(r => r.TextContent.Contains("edge-01")).Click();

        await cut.InvokeAsync(() => cut.Find(ChartButtonSelector).Click());

        await _dialogService.Received(1).ShowAsync<QuickAddToChartDialog>(
            Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>());
        _snackbar.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<Severity>(),
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }
}
