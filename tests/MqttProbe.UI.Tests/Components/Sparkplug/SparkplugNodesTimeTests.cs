using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesTimeTests : BunitTestContext
{
    private static readonly DateTime _birthUtc = new(2026, 10, 25, 0, 55, 7, 250, DateTimeKind.Utc);
    private static readonly DateTime _dataUtc = new(2026, 10, 25, 12, 30, 5, 125, DateTimeKind.Utc);

    private RecordingUiSettings _ui = null!;
    private SparkplugTimeFormatterStub _formatter = null!;
    private IRenderedComponent<MudPopoverProvider> _popovers = null!;
    private SpbNode _node = null!;

    [SetUp]
    public void SetupMocks()
    {
        _node = new SpbNode
        {
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online,
            LastBirthAt = _birthUtc,
            LastDataAt = _dataUtc,
            Metrics =
            [
                new SpbMetricSnapshot("Flow Rate", "double", "12.5000", _dataUtc),
                new SpbMetricSnapshot("Pressure", "double", "1.0000", _birthUtc)
            ]
        };
        var group = new SpbGroup { GroupId = "factory" };
        group.Nodes[_node.NodeId] = _node;

        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });
        Services.AddSingleton(topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(sparkplug);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());

        _ui = new RecordingUiSettings();
        Services.AddUiSettings(_ui);
        _formatter = new SparkplugTimeFormatterStub();
        Services.AddSingleton<IDeviceTimeFormatter>(_formatter);

        _popovers = EnsureMudProviders();
    }

    private IRenderedComponent<SparkplugNodesView> RenderSelectedNode()
    {
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-node-row")[0].Click();
        return cut;
    }

    private static async Task ShowFlatAsync(IRenderedComponent<SparkplugNodesView> cut)
    {
        var metricSwitch = cut.FindComponent<MudSwitch<bool>>();
        await cut.InvokeAsync(() => metricSwitch.Instance.ValueChanged.InvokeAsync(false));
        cut.Render();
    }

    [Test]
    public void NodesRegion_ShowsOneShortZoneCaptionWithFullDetailOnDemand()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneLocal };
        _formatter.ZoneLabel = "Europe/London";
        _formatter.Format = static (request, _) => DetailAt(request);

        var cut = RenderSelectedNode();

        cut.WaitForAssertion(() =>
        {
            var caption = cut.FindAll("[data-time-zone-label]");
            caption.Should().ContainSingle("the zone is named once per Nodes region");
            caption[0].TextContent.Trim().Should().Be(
                "GMT",
                "the caption follows the newest instant and shows the short abbreviation, not the IANA path");
            cut.FindAll(".spb-timestamp__value .spb-zone-token")
                .Select(token => token.TextContent.Trim())
                .Should().Equal(["BST", "GMT"], "each timestamp keeps its own DST abbreviation");
        }, TimeSpan.FromSeconds(5));

        TooltipFor(cut.Find("[data-time-zone-label]"))
            .Should().Be("Europe/London · Greenwich Mean Time · UTC+00:00");
    }

    [Test]
    public void DetailTimestamps_ShowOnlyEachInstantsAbbreviation()
    {
        var display = new SparkplugTimeDisplay(
            "Europe/London",
            new Dictionary<string, DeviceTimeFormatItem>(StringComparer.Ordinal)
            {
                [SparkplugTimeKeys.NodeBirth] = new(
                    Guid.NewGuid(), "01:55:07.250", "2026-10-25",
                    "2026-10-25 01:55:07.250 BST", "BST", "British Summer Time", "UTC+01:00"),
                [SparkplugTimeKeys.NodeLastData] = new(
                    Guid.NewGuid(), "12:30:05.125", "2026-10-25",
                    "2026-10-25 12:30:05.125 GMT", "GMT", "Greenwich Mean Time", "UTC+00:00")
            });

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, _node)
            .Add(component => component.Time, display));

        cut.FindAll(".spb-timestamp__time").Select(element => element.TextContent.Trim())
            .Should().Equal("01:55:07.250", "12:30:05.125");

        var tokens = cut.FindAll(".spb-timestamp__value .spb-zone-token");
        tokens.Select(token => token.TextContent.Trim()).Should().Equal("BST", "GMT");
        tokens[0].GetAttribute("aria-label")
            .Should().Be("BST: Europe/London · British Summer Time · UTC+01:00",
                "the exact offset stays available without being printed beside every timestamp");
    }

    [Test]
    public void NodesDetail_ShowsUtcAbbreviationAndKeepsTheOffsetAccessible()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        _formatter.ZoneLabel = "UTC";
        _formatter.Format = static (request, _) => new DeviceTimeFormatItem(
            request.Key,
            $"t{request.EpochMs}",
            "2026-10-25",
            $"full {request.EpochMs}",
            "UTC",
            request.Detail ? "Coordinated Universal Time" : "",
            request.Detail ? "UTC+00:00" : "");

        var cut = RenderSelectedNode();

        cut.WaitForAssertion(() => cut.FindAll(".spb-timestamp__value .spb-zone-token")
            .Select(token => token.TextContent.Trim())
            .Should().Equal("UTC", "UTC"), TimeSpan.FromSeconds(5));

        var token = cut.Find(".spb-timestamp__value .spb-zone-token");
        TooltipFor(token).Should().Be(
            "UTC · Coordinated Universal Time · UTC+00:00",
            "the exact UTC offset is discoverable on focus, not only announced");
        token.GetAttribute("aria-label").Should().Contain("UTC+00:00");
    }

    private static DeviceTimeFormatItem DetailAt(DeviceTimeFormatRequest request)
    {
        var summer = request.EpochMs == EpochMillis(_birthUtc);
        return new DeviceTimeFormatItem(
            request.Key,
            $"t{request.EpochMs}",
            "2026-10-25",
            $"full {request.EpochMs}",
            summer ? "BST" : "GMT",
            request.Detail ? (summer ? "British Summer Time" : "Greenwich Mean Time") : "",
            request.Detail ? (summer ? "UTC+01:00" : "UTC+00:00") : "");
    }

    private string TooltipFor(IElement token)
    {
        token.Focus();
        _popovers.WaitForAssertion(() =>
            _popovers.FindAll(".mud-tooltip.mud-popover-open").Should().ContainSingle());
        return NormalizeText(_popovers.Find(".mud-tooltip.mud-popover-open"));
    }

    private static string NormalizeText(IElement element) =>
        string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Test]
    public void MetricsTable_ShowsDeviceTimeWithoutPerRowZoneSuffix()
    {
        var display = MetricTimeDisplay("12:30:05.125", "2026-10-25 12:30:05.125 UTC");

        var cut = RenderMetricsTable(display);

        var cell = cut.Find(".spb-td-updated");
        cell.TextContent.Trim().Should().Be("12:30:05.125");
        cell.QuerySelector("[data-time-zone]").Should().BeNull("rows never repeat the zone suffix");
        cell.QuerySelector("[data-time-value]")!.GetAttribute("title")
            .Should().Be("2026-10-25 12:30:05.125 UTC");
    }

    [Test]
    public void MetricsTable_ShowsAPendingMarkerUntilTheDeviceFormatsTheInstant()
    {
        var cut = RenderMetricsTable(new SparkplugTimeDisplay("UTC",
            new Dictionary<string, DeviceTimeFormatItem>(StringComparer.Ordinal)));

        var value = cut.Find(".spb-td-updated [data-time-value]");
        value.TextContent.Trim().Should().Be("…");
        value.GetAttribute("data-time-pending").Should().Be("true");
        value.GetAttribute("title").Should().Be("Waiting for the viewing device clock");
    }

    [Test]
    public async Task HierarchyMetric_UsesExactFullNameTimeAndChartIdentityAcrossModes()
    {
        const string topic = "spBv1.0/factory/NDATA/edge-01";
        const string fullName = "Deep/One/Two";
        const string timeText = "12:30:05.125";
        var display = new SparkplugTimeDisplay("UTC",
            new Dictionary<string, DeviceTimeFormatItem>(StringComparer.Ordinal)
            {
                [SparkplugTimeKeys.Metric(topic, fullName)] = new(
                    Guid.NewGuid(), timeText, "2026-10-25",
                    "2026-10-25 12:30:05.125 UTC", "UTC", "", "UTC+00:00")
            });
        SparkplugChartSelection? captured = null;

        var cut = Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items,
                [new SpbMetricSnapshot(fullName, "double", "7.5000", _dataUtc)])
            .Add(component => component.Topic, topic)
            .Add(component => component.Time, display)
            .Add(component => component.Mode, SparkplugMetricViewMode.Hierarchy)
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, selection => captured = selection)));

        AssertMetricSurface(cut, fullName, "Two", timeText);
        await ClickChartAsync(cut);
        AssertChartSelection(captured, topic, fullName);

        cut.Render(parameters => parameters.Add(component => component.Mode, SparkplugMetricViewMode.Flat));
        AssertMetricSurface(cut, fullName, fullName, timeText);
        await ClickChartAsync(cut);
        AssertChartSelection(captured, topic, fullName);

        cut.Render(parameters => parameters.Add(component => component.Mode, SparkplugMetricViewMode.Hierarchy));
        AssertMetricSurface(cut, fullName, "Two", timeText);
    }

    private static void AssertMetricSurface(
        IRenderedComponent<SparkplugMetricsTable> cut,
        string fullName,
        string label,
        string timeText)
    {
        var row = cut.FindAll("tr.spb-metric-row")
            .Single(item => item.QuerySelector(".spb-td-name")!.GetAttribute("title") == fullName);
        row.QuerySelector(".spb-metric-label")!.TextContent.Trim().Should().Be(label);
        row.QuerySelector(".spb-td-value")!.TextContent.Trim().Should().Be("7.5000");
        row.QuerySelector(".spb-td-updated")!.TextContent.Trim().Should().Be(timeText);
        row.QuerySelector(".spb-metric-meta-updated")!.TextContent.Trim().Should().Be(timeText);
        cut.FindAll(".spb-time-value").Should().ContainSingle(
            "the compact meta line must not duplicate the authoritative time node");
    }

    private static async Task ClickChartAsync(IRenderedComponent<SparkplugMetricsTable> cut) =>
        await cut.InvokeAsync(() => cut.Find("button[title='Add to chart']").Click());

    private static void AssertChartSelection(
        SparkplugChartSelection? selection,
        string topic,
        string fullName)
    {
        selection.Should().NotBeNull();
        selection!.Topic.Should().Be(topic);
        selection.JsonPath.Should().Be($"metrics.{fullName}");
        selection.SeriesName.Should().Be(fullName);
    }

    [Test]
    public async Task DuplicateNodeMetricNames_KeepTheRegionRenderable()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        var latest = _dataUtc.AddMinutes(5);
        _node.Metrics =
        [
            new SpbMetricSnapshot("Flow Rate", "double", "12.5000", _dataUtc),
            new SpbMetricSnapshot("Flow Rate", "double", "13.2500", latest)
        ];
        _formatter.Format = static (request, _) => new DeviceTimeFormatItem(
            request.Key, $"t{request.EpochMs}", "", "", "UTC", "", "");

        var cut = RenderSelectedNode();
        await ShowFlatAsync(cut);

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindAll(".spb-time-value");
            cells.Should().HaveCount(2, "the duplicated metric name still renders both rows");
            cells.Select(cell => cell.TextContent.Trim())
                .Should().AllBe($"t{EpochMillis(latest)}", "both rows read the snapshot that wins for the name");
            cut.FindAll("tr.spb-metric-row .spb-td-value").Select(cell => cell.TextContent.Trim())
                .Should().AllBe("13.2500");
            cut.Find("[data-time-zone-label]").Should().NotBeNull("the region caption still resolves");
        }, TimeSpan.FromSeconds(5));

        _formatter.Batches.Should().ContainSingle("the region still formats in one batch");
        _formatter.Batches[0].Requests.Select(request => request.Key)
            .Should().OnlyHaveUniqueItems("a duplicated metric name must not reuse a request key in one batch");
    }

    [Test]
    public async Task DuplicateDeviceMetricNames_KeepTheDeviceTableRenderable()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        var latest = _dataUtc.AddMinutes(5);
        _node.Devices["sensor-A"] = new SpbDevice
        {
            DeviceId = "sensor-A",
            NodeId = _node.NodeId,
            GroupId = _node.GroupId,
            Status = SpbNodeStatus.Online,
            LastBirthAt = _birthUtc,
            LastDataAt = latest,
            Metrics =
            [
                new SpbMetricSnapshot("Flow Rate", "double", "12.5000", _dataUtc),
                new SpbMetricSnapshot("Flow Rate", "double", "13.2500", latest)
            ]
        };
        _formatter.Format = static (request, _) => new DeviceTimeFormatItem(
            request.Key, $"t{request.EpochMs}", "", "", "UTC", "", "");

        var cut = RenderSelectedNode();
        await ShowFlatAsync(cut);
        cut.Find(".spb-device-select").Click();

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindAll(".spb-device-metrics .spb-time-value");
            cells.Should().HaveCount(2, "the duplicated device metric name still renders both rows");
            cells.Select(cell => cell.TextContent.Trim())
                .Should().AllBe($"t{EpochMillis(latest)}", "both device rows read the snapshot that wins for the name");
        }, TimeSpan.FromSeconds(5));

        _formatter.Batches.Should().HaveCount(2, "one batch for the node and one for the expanded device");
        _formatter.Batches.Should().OnlyContain(batch =>
            batch.Requests.Select(request => request.Key).Distinct().Count() == batch.Requests.Count);
    }

    [Test]
    public void NodesRegion_FormatsNodeTimesAndMetricRowsInOneBatch()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        _formatter.Format = static (request, _) => new DeviceTimeFormatItem(
            request.Key, request.Detail ? $"node {request.EpochMs}" : $"row {request.EpochMs}",
            "", "", "", "", "");

        var cut = RenderSelectedNode();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".spb-time-value").Select(element => element.TextContent.Trim())
                .Should().Equal($"row {EpochMillis(_dataUtc)}", $"row {EpochMillis(_birthUtc)}");
            cut.Find("[data-time-value='node:last-data']").TextContent.Trim()
                .Should().Be($"node {EpochMillis(_dataUtc)}");
        }, TimeSpan.FromSeconds(5));

        _formatter.Batches.Should().ContainSingle("the whole Nodes region formats in one batch, never per row");
        var requests = _formatter.Batches[0].Requests;
        requests.Should().HaveCount(5, "two node timestamps, two metric rows and the region zone caption");
        requests.Count(request => request.Detail).Should().Be(3, "node timestamps and the caption carry zone detail");
        requests.Count(request => !request.Detail).Should().Be(2, "metric rows stay quiet");
    }

    [Test]
    public void SameStoredInstant_RendersTheSameInTheNodeTimestampAndItsMetricRow()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences { Zone = MessageTimePreferences.ZoneUtc };
        _formatter.Format = static (request, _) => new DeviceTimeFormatItem(
            request.Key, $"t{request.EpochMs}", "", "", "", "", "");

        var cut = RenderSelectedNode();

        cut.WaitForAssertion(() =>
        {
            var lastData = cut.Find("[data-time-value='node:last-data']").TextContent.Trim();
            lastData.Should().Be($"t{EpochMillis(_dataUtc)}");
            MetricUpdated(cut, "Flow Rate").Should().Be(lastData,
                "one stored instant reads the same on every Nodes surface");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ChangingTimePreferences_ReformatsExistingValuesWithOneMoreBatch()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat24Hour
        };
        _formatter.Format = static (request, preferences) => new DeviceTimeFormatItem(
            request.Key,
            preferences.ClockFormat == MessageTimePreferences.ClockFormat12Hour
                ? $"12h {request.EpochMs}"
                : $"24h {request.EpochMs}",
            "", "",
            preferences.Zone == MessageTimePreferences.ZoneUtc ? "UTC" : "EST",
            "", "");

        var cut = RenderSelectedNode();
        cut.WaitForAssertion(() => cut.FindAll(".spb-time-value").Should()
            .OnlyContain(value => value.TextContent.StartsWith("24h")), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => _ui.SetMessageTimeAsync(new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        }));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".spb-time-value").Should()
                .OnlyContain(value => value.TextContent.StartsWith("12h"));
            cut.Find("[data-time-zone-label]").TextContent.Trim().Should().Be("EST");
        }, TimeSpan.FromSeconds(5));

        _formatter.Batches.Should().HaveCount(2, "one further batch covers the whole region, never one call per row");
    }

    [Test]
    public async Task PreferenceChangeDuringABatch_KeepsOnlyTheNewFormat()
    {
        _ui.Ui.MessageTime = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat24Hour
        };
        _formatter.Format = static (request, preferences) => new DeviceTimeFormatItem(
            request.Key,
            preferences.ClockFormat == MessageTimePreferences.ClockFormat12Hour
                ? $"12h {request.EpochMs}"
                : $"24h {request.EpochMs}",
            "", "", "", "", "");
        var gate = new TaskCompletionSource();
        _formatter.FirstCallGate = gate;

        var cut = RenderSelectedNode();
        cut.WaitForAssertion(() => _formatter.Batches.Should().ContainSingle(), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => _ui.SetMessageTimeAsync(new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneUtc,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        }));

        gate.SetResult();

        cut.WaitForAssertion(() => cut.FindAll(".spb-time-value").Should()
            .OnlyContain(value => value.TextContent.StartsWith("12h")), TimeSpan.FromSeconds(5));
        cut.Find("[data-time-value='node:last-data']").TextContent.Trim()
            .Should().StartWith("12h", "the superseded 24-hour batch must not overwrite the new format");
    }

    private static long EpochMillis(DateTime instant) =>
        new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static string MetricUpdated(IRenderedComponent<SparkplugNodesView> cut, string name) =>
        cut.FindAll("tr.spb-metric-row")
            .Single(row => row.QuerySelector(".spb-metric-label")!.TextContent.Trim() == name)
            .QuerySelector(".spb-td-updated")!.TextContent.Trim();

    private SparkplugTimeDisplay MetricTimeDisplay(string timeText, string fullText) =>
        new("UTC",
            new Dictionary<string, DeviceTimeFormatItem>(StringComparer.Ordinal)
            {
                [SparkplugTimeKeys.Metric("spBv1.0/factory/NDATA/edge-01", "Flow Rate")] = new(
                    Guid.NewGuid(), timeText, "2026-10-25", fullText, "UTC", "", "UTC+00:00")
            });

    private IRenderedComponent<SparkplugMetricsTable> RenderMetricsTable(SparkplugTimeDisplay display) =>
        Render<SparkplugMetricsTable>(parameters => parameters
            .Add(component => component.Items,
                new[] { new SpbMetricSnapshot("Flow Rate", "double", "12.5000", _dataUtc) })
            .Add(component => component.Topic, "spBv1.0/factory/NDATA/edge-01")
            .Add(component => component.Time, display)
            .Add(component => component.OnAddToChart,
                EventCallback.Factory.Create<SparkplugChartSelection>(this, _ => { })));
}
