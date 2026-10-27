using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.PluginContracts;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesViewTopologyAddToChartTests : BunitTestContext
{
    private const string AddToChartSelector = "button[title='Add to chart']";
    private const int DormantRenderWindowMs = 600_000;
    private const int HighRateBurstSize = 64;
    private const string Node = "edge-01";
    private const string Device = "sensor-A";
    private const string NodeBirthTopic = "spBv1.0/factory/NBIRTH/edge-01";
    private const string NodeDataTopic = "spBv1.0/factory/NDATA/edge-01";
    private const string DeviceBirthTopic = "spBv1.0/factory/DBIRTH/edge-01/sensor-A";
    private const string DeviceDataTopic = "spBv1.0/factory/DDATA/edge-01/sensor-A";

    private SparkplugTopologyService _topology = null!;
    private SparkplugTopologyExtractor _extractor = null!;

    [SetUp]
    public void SetupTopology()
    {
        _topology = new SparkplugTopologyService();
        _extractor = new SparkplugTopologyExtractor();
        Services.AddSingleton<ISparkplugTopologyService>(_topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var settings = Substitute.For<ISparkplugSettings>();
        settings.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(settings);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        EnsureMudProviders();
    }

    [Test]
    public async Task NodeAliasOnlyTypeOmission_KeepsNativeChartButtonsAndExcludesStrings()
    {
        await FireAsync(NodeBirthTopic, NodeBirthPayload());
        var cut = RenderNode();

        AssertChartButtons(cut, "Node Numeric", "Node Switch", "Node Timestamp");
        AssertNoChartButtons(cut, "Node String Number", "Node String Bool");

        await FireAsync(NodeDataTopic, NodeDataPayload(sequence: 1));

        cut.WaitForAssertion(() => MetricValue(cut, "Node Numeric").Should().Be("25.0000"),
            TimeSpan.FromSeconds(5));
        AssertChartButtons(cut, "Node Numeric", "Node Switch", "Node Timestamp");
        AssertNoChartButtons(cut, "Node String Number", "Node String Bool");
    }

    [Test]
    public async Task ExpandedDeviceAliasOnlyTypeOmission_KeepsNativeChartButtonsAndExcludesStrings()
    {
        await FireAsync(NodeBirthTopic, NodeBirthPayload());
        await FireAsync(DeviceBirthTopic, DeviceBirthPayload());
        var cut = RenderNode();
        ExpandDevice(cut);

        AssertChartButtons(cut, "Device Numeric", "Device Switch", "Device Timestamp");
        AssertNoChartButtons(cut, "Device String Number", "Device String Bool");

        await FireAsync(DeviceDataTopic, DeviceDataPayload(sequence: 2));

        cut.WaitForAssertion(() => MetricValue(cut, "Device Numeric").Should().Be("31.5000"),
            TimeSpan.FromSeconds(5));
        AssertChartButtons(cut, "Device Numeric", "Device Switch", "Device Timestamp");
        AssertNoChartButtons(cut, "Device String Number", "Device String Bool");
    }

    [Test]
    public async Task NodeMetricRow_AliasOnlyValueUpdate_RetainsTheSameChartButtonComponent()
    {
        await FireAsync(NodeBirthTopic, NodeBirthPayload());
        var cut = RenderNode();
        HoldRefreshes(cut);
        var before = ChartButton(cut, "Node Numeric").Instance;

        await FireAsync(NodeDataTopic, NodeDataPayload(sequence: 1));
        cut.Instance.RenderCoalescerForTest.Tick();

        cut.WaitForAssertion(() => MetricValue(cut, "Node Numeric").Should().Be("25.0000"),
            TimeSpan.FromSeconds(5));
        var after = ChartButton(cut, "Node Numeric");
        after.Instance.Should().BeSameAs(before,
            "the row keeps its chart button while the value changes");
        after.Instance.JsonPath.Should().Be("metrics.Node Numeric");
        after.Instance.SeriesName.Should().Be("Node Numeric");
    }

    [Test]
    public async Task NodeMetricRow_HighRateAliasOnlyBurst_ShowsLatestValueOnTheRetainedChartButton()
    {
        await FireAsync(NodeBirthTopic, NodeBirthPayload());
        var cut = RenderNode();
        HoldRefreshes(cut);
        var before = ChartButton(cut, "Node Numeric").Instance;

        for (var i = 0; i < HighRateBurstSize; i++)
            await FireAsync(NodeDataTopic, AliasDoublePayload((ulong)(i + 1), 11, 100 + i));

        cut.Instance.RenderCoalescerForTest.Tick();

        cut.WaitForAssertion(() => MetricValue(cut, "Node Numeric").Should().Be("163.0000"),
            TimeSpan.FromSeconds(5));
        ChartButton(cut, "Node Numeric").Instance.Should().BeSameAs(before,
            "one coalesced refresh keeps the row identity while showing the last value");
    }

    [Test]
    public async Task DeviceMetricRow_AliasOnlyValueUpdate_RetainsTheSameChartButtonComponent()
    {
        await FireAsync(NodeBirthTopic, NodeBirthPayload());
        await FireAsync(DeviceBirthTopic, DeviceBirthPayload());
        var cut = RenderNode();
        ExpandDevice(cut);
        HoldRefreshes(cut);
        var before = ChartButton(cut, "Device Numeric").Instance;

        await FireAsync(DeviceDataTopic, DeviceDataPayload(sequence: 2));
        cut.Instance.RenderCoalescerForTest.Tick();

        cut.WaitForAssertion(() => MetricValue(cut, "Device Numeric").Should().Be("31.5000"),
            TimeSpan.FromSeconds(5));
        var after = ChartButton(cut, "Device Numeric");
        after.Instance.Should().BeSameAs(before,
            "the expanded device row keeps its chart button while the value changes");
        after.Instance.JsonPath.Should().Be("metrics.Device Numeric");
        after.Instance.SeriesName.Should().Be("Device Numeric");
    }

    [Test]
    public async Task NodeMetricRow_AliasOnlyNonNumericValue_DropsOnlyThatChartButton()
    {
        await FireAsync(NodeBirthTopic, NodeBirthPayload());
        var cut = RenderNode();
        HoldRefreshes(cut);

        await FireAsync(NodeDataTopic, AliasStringPayload(sequence: 1, alias: 11, value: "not-a-number"));
        cut.Instance.RenderCoalescerForTest.Tick();

        cut.WaitForAssertion(() => MetricValue(cut, "Node Numeric").Should().Be("not-a-number"),
            TimeSpan.FromSeconds(5));
        ChartButtonOrNull(cut, "Node Numeric").Should().BeNull("a non-numeric value is not chartable");
        ChartButton(cut, "Node Switch").Instance.JsonPath.Should().Be("metrics.Node Switch",
            "the other metrics stay chartable");
    }

    private static void HoldRefreshes(IRenderedComponent<SparkplugNodesView> cut) =>
        cut.Instance.RenderCoalescerForTest.WindowMs = DormantRenderWindowMs;

    private static IRenderedComponent<AddToChartButton> ChartButton(
        IRenderedComponent<SparkplugNodesView> cut, string metricName) =>
        ChartButtonOrNull(cut, metricName)
        ?? throw new InvalidOperationException($"No add-to-chart button for {metricName}");

    private static IRenderedComponent<AddToChartButton>? ChartButtonOrNull(
        IRenderedComponent<SparkplugNodesView> cut, string metricName)
    {
        var jsonPath = SparkplugChartTargets.JsonPath(metricName);
        return cut.FindComponents<AddToChartButton>().SingleOrDefault(button => button.Instance.JsonPath == jsonPath);
    }

    private async Task FireAsync(string topic, Payload payload)
    {
        var bytes = payload.ToByteArray();
        var envelope = DecodedPayloadEnvelope.CreateSuccess(
            "sparkplug-b", topic, bytes, string.Empty, typedPayload: payload);
        await _topology.ApplyTopologyEventsAsync(_extractor.Extract(envelope));
    }

    private IRenderedComponent<SparkplugNodesView> RenderNode()
    {
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row").First(row => row.TextContent.Contains(Node)).Click();
        return cut;
    }

    private static void ExpandDevice(IRenderedComponent<SparkplugNodesView> cut) =>
        cut.FindAll(".spb-device-row").First(row => row.TextContent.Contains(Device)).Click();

    private static void AssertChartButtons(IRenderedComponent<SparkplugNodesView> cut, params string[] names)
    {
        foreach (var name in names)
            MetricRow(cut, name).QuerySelector(AddToChartSelector).Should().NotBeNull($"{name} remains chartable");
    }

    private static void AssertNoChartButtons(IRenderedComponent<SparkplugNodesView> cut, params string[] names)
    {
        foreach (var name in names)
            MetricRow(cut, name).QuerySelector(AddToChartSelector).Should().BeNull($"{name} is not chartable");
    }

    private static string MetricValue(IRenderedComponent<SparkplugNodesView> cut, string metricName) =>
        MetricRow(cut, metricName).QuerySelector(".spb-td-value")!.TextContent.Trim();

    private static AngleSharp.Dom.IElement MetricRow(
        IRenderedComponent<SparkplugNodesView> cut, string metricName) =>
        cut.FindAll("tr.spb-metric-row").Single(row =>
            row.QuerySelector(".spb-td-name")?.TextContent.Trim() == metricName);

    private static Payload NodeBirthPayload()
    {
        var payload = new Payload { Seq = 0, Timestamp = Now() };
        payload.Metrics.Add(DoubleMetric("Node Numeric", 11, 10, 20.0));
        payload.Metrics.Add(BooleanMetric("Node Switch", 12, 11, true));
        payload.Metrics.Add(StringMetric("Node String Number", 13, 12, "42"));
        payload.Metrics.Add(StringMetric("Node String Bool", 14, 12, "true"));
        payload.Metrics.Add(LongMetric("Node Timestamp", 15, 14, 1712345678000UL));
        return payload;
    }

    private static Payload NodeDataPayload(ulong sequence)
    {
        var payload = new Payload { Seq = sequence, Timestamp = Now() };
        payload.Metrics.Add(AliasDouble(11, 25.0));
        payload.Metrics.Add(AliasBoolean(12, false));
        payload.Metrics.Add(AliasString(13, "42"));
        payload.Metrics.Add(AliasString(14, "true"));
        payload.Metrics.Add(AliasLong(15, 1712345679000UL));
        return payload;
    }

    private static Payload DeviceBirthPayload()
    {
        var payload = new Payload { Seq = 1, Timestamp = Now() };
        payload.Metrics.Add(DoubleMetric("Device Numeric", 21, 10, 30.0));
        payload.Metrics.Add(BooleanMetric("Device Switch", 22, 11, true));
        payload.Metrics.Add(StringMetric("Device String Number", 23, 12, "42"));
        payload.Metrics.Add(StringMetric("Device String Bool", 24, 12, "true"));
        payload.Metrics.Add(LongMetric("Device Timestamp", 25, 14, 1712345678000UL));
        return payload;
    }

    private static Payload DeviceDataPayload(ulong sequence)
    {
        var payload = new Payload { Seq = sequence, Timestamp = Now() };
        payload.Metrics.Add(AliasDouble(21, 31.5));
        payload.Metrics.Add(AliasBoolean(22, false));
        payload.Metrics.Add(AliasString(23, "42"));
        payload.Metrics.Add(AliasString(24, "true"));
        payload.Metrics.Add(AliasLong(25, 1712345679000UL));
        return payload;
    }

    private static Payload AliasDoublePayload(ulong sequence, ulong alias, double value)
    {
        var payload = new Payload { Seq = sequence, Timestamp = Now() };
        payload.Metrics.Add(AliasDouble(alias, value));
        return payload;
    }

    private static Payload AliasStringPayload(ulong sequence, ulong alias, string value)
    {
        var payload = new Payload { Seq = sequence, Timestamp = Now() };
        payload.Metrics.Add(AliasString(alias, value));
        return payload;
    }

    private static Payload.Types.Metric DoubleMetric(string name, ulong alias, uint datatype, double value) =>
        new() { Name = name, Alias = alias, Datatype = datatype, DoubleValue = value };

    private static Payload.Types.Metric BooleanMetric(string name, ulong alias, uint datatype, bool value) =>
        new() { Name = name, Alias = alias, Datatype = datatype, BooleanValue = value };

    private static Payload.Types.Metric StringMetric(string name, ulong alias, uint datatype, string value) =>
        new() { Name = name, Alias = alias, Datatype = datatype, StringValue = value };

    private static Payload.Types.Metric LongMetric(string name, ulong alias, uint datatype, ulong value) =>
        new() { Name = name, Alias = alias, Datatype = datatype, LongValue = value };

    private static Payload.Types.Metric AliasDouble(ulong alias, double value) =>
        new() { Alias = alias, DoubleValue = value };

    private static Payload.Types.Metric AliasBoolean(ulong alias, bool value) =>
        new() { Alias = alias, BooleanValue = value };

    private static Payload.Types.Metric AliasString(ulong alias, string value) =>
        new() { Alias = alias, StringValue = value };

    private static Payload.Types.Metric AliasLong(ulong alias, ulong value) =>
        new() { Alias = alias, LongValue = value };

    private static ulong Now() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
