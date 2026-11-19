using System.Globalization;
using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Layout;

[TestFixture]
public class MetricsStatsChipRendererCallsTests : BunitTestContext
{
    private IUxMetricsService _mockMetrics = null!;

    [SetUp]
    public void Setup()
    {
        _mockMetrics = Substitute.For<IUxMetricsService>();
        var mqttClient = Substitute.For<IMqttManagedClient>();
        mqttClient.IsConnected.Returns(false);

        Services.AddSingleton(_mockMetrics);
        Services.AddSingleton(mqttClient);
        Services.AddSingleton(Substitute.For<IMessageStoreManager>());

        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var registry = builder.Build();
        var pipeline = new PayloadPipeline(registry, Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>());
        Services.AddSingleton(pipeline);
        Services.AddSingleton<IFormatDisplayNames>(new FormatDisplayNames(pipeline));
    }

    private static UxMetricsSnapshot CreateSnapshot(params ChartRendererCallMetricsSnapshot[] rendererCalls) =>
        new(
            ConnectAttempts: 0, ConnectSuccesses: 0, ConnectFailures: 0,
            PublishSuccesses: 0, PublishFailures: 0,
            ChartsCreated: 0, SeriesAddedToExistingCharts: 0,
            MessagesProcessed: 0, MessagesDropped: 0,
            AvgProcessingTimeUs: 0, MaxProcessingTimeUs: 0,
            AvgPayloadBytes: 0, MaxPayloadBytes: 0,
            CurrentMessagesPerSecond: 0,
            MessageRateHistory: new int[UxMetricsService.RateWindowSeconds],
            MessagesProcessedByFormat: new Dictionary<string, long>(),
            ChartFunnelBySource: new Dictionary<string, long>(),
            MaxDisplayMessages: 100, CurrentDisplayedMessageCount: 0,
            AppHealth: new AppHealthMetricsSnapshot(
                CpuUsagePercent: 0, ManagedHeapMb: 0,
                WorkingSetMb: 0, ThreadCount: 0, ThreadPoolQueueLength: 0,
                GcGen2Collections: 0, UptimeSeconds: 0),
            EmulatorPublishersOnline: 0,
            EmulatorPublishCycles: 0, EmulatorNodesInError: 0,
            ChartRendererCalls: rendererCalls.Length == 0 ? null : rendererCalls);

    private static IElement FindPanel(IRenderedComponent<MudPopoverProvider> provider, string title) =>
        provider.FindAll(".mud-expand-panel")
            .Single(p => p.QuerySelector(".mud-expand-panel-text")?.TextContent.Trim() == title);

    private static IElement FindRenderer(IElement panel, string renderer) =>
        panel.QuerySelectorAll(".metrics-subsection")
            .Single(s => s.QuerySelector(".metrics-subsection-title")?.TextContent.Trim() == renderer);

    private static string RowValue(IElement scope, string label) =>
        scope.QuerySelectorAll(".metrics-row")
            .Single(row => row.QuerySelector("span")?.TextContent.Trim() == label)
            .QuerySelectorAll("span")[1]
            .TextContent.Trim();

    private (IRenderedComponent<MudPopoverProvider> Provider, IRenderedComponent<MetricsStatsChip> Chip) OpenFlyout()
    {
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();
        cut.Find("button.mud-chip").Click();
        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Traffic"));
        return (provider, cut);
    }

    [Test]
    public void ChartsPanel_IsHidden_WhenNoRendererCallsReported()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot());
        var (provider, _) = OpenFlyout();

        provider.WaitForAssertion(() =>
        {
            var titles = provider.FindAll(".mud-expand-panel-text").Select(e => e.TextContent.Trim()).ToList();
            titles.Should().Equal("Processing", "Resources", "System");
            provider.Markup.Should().NotContain("Completed update calls");
            provider.Markup.Should().NotContain("Call time (avg / max)");
        });
    }

    [Test]
    public void ChartsPanel_ShowsBothRenderers_WithCountsFailuresAndCallTimes()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(
            new ChartRendererCallMetricsSnapshot("Apex", CompletedCalls: 120, FailedCalls: 3,
                AverageCallMilliseconds: 2.25, MaximumCallMilliseconds: 12.5, LastCompletedCallAgeSeconds: 4),
            new ChartRendererCallMetricsSnapshot("ECharts", CompletedCalls: 60, FailedCalls: 1,
                AverageCallMilliseconds: 8.4, MaximumCallMilliseconds: 25, LastCompletedCallAgeSeconds: 75)));
        var (provider, _) = OpenFlyout();

        provider.WaitForAssertion(() =>
        {
            var charts = FindPanel(provider, "Charts");

            var apex = FindRenderer(charts, "Apex");
            RowValue(apex, "Completed update calls").Should().Be("120");
            RowValue(apex, "Failed calls").Should().Be("3");
            RowValue(apex, "Call time (avg / max)").Should().Be(
                $"{2.25.ToString("N2", CultureInfo.CurrentCulture)} / {12.5.ToString("N1", CultureInfo.CurrentCulture)} ms");
            RowValue(apex, "Last completed call").Should().Be("4s ago");
            apex.QuerySelectorAll(".metrics-error").Should().NotBeEmpty();

            var echarts = FindRenderer(charts, "ECharts");
            RowValue(echarts, "Completed update calls").Should().Be("60");
            RowValue(echarts, "Failed calls").Should().Be("1");
            RowValue(echarts, "Last completed call").Should().Be("1m 15s ago");
            echarts.QuerySelectorAll(".metrics-error").Should().NotBeEmpty();

            charts.TextContent.Should().Contain("Average and maximum cover the last 64 completed update calls, including failures.");
            charts.TextContent.Should().Contain("Call time measures the wait for the renderer API to return, not paint or end-to-end latency.");
            charts.TextContent.Should().Contain("Counts are totals for this session. Failed calls are included in completed calls.");
        });
    }

    [Test]
    public void ChartsPanel_ShowsOnlyReportedRenderer()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(
            new ChartRendererCallMetricsSnapshot("ECharts", CompletedCalls: 10, FailedCalls: 0,
                AverageCallMilliseconds: 1.5, MaximumCallMilliseconds: 3, LastCompletedCallAgeSeconds: 12)));
        var (provider, _) = OpenFlyout();

        provider.WaitForAssertion(() =>
        {
            var charts = FindPanel(provider, "Charts");
            charts.TextContent.Should().Contain("ECharts");
            charts.TextContent.Should().NotContain("Apex");

            var echarts = FindRenderer(charts, "ECharts");
            RowValue(echarts, "Completed update calls").Should().Be("10");
            RowValue(echarts, "Failed calls").Should().Be("0");
            RowValue(echarts, "Last completed call").Should().Be("12s ago");
            echarts.QuerySelectorAll(".metrics-error").Should().BeEmpty();
        });
    }

    [Test]
    public void ChartsPanel_ShowsNoDataPlaceholders_WhenRendererHasNoReturnedCalls()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(
            new ChartRendererCallMetricsSnapshot("Apex", CompletedCalls: 0, FailedCalls: 0,
                AverageCallMilliseconds: 0, MaximumCallMilliseconds: 0, LastCompletedCallAgeSeconds: 0)));
        var (provider, _) = OpenFlyout();

        provider.WaitForAssertion(() =>
        {
            var apex = FindRenderer(FindPanel(provider, "Charts"), "Apex");
            RowValue(apex, "Call time (avg / max)").Should().Be("N/A");
            RowValue(apex, "Last completed call").Should().Be("N/A");
        });
    }

    [Test]
    public void ChartsPanel_CollapsesOnMenuReopen_AfterBeingExpanded()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(
            new ChartRendererCallMetricsSnapshot("Apex", CompletedCalls: 5, FailedCalls: 0,
                AverageCallMilliseconds: 2, MaximumCallMilliseconds: 4, LastCompletedCallAgeSeconds: 1)));
        var (provider, cut) = OpenFlyout();

        var chartsHeader = FindPanel(provider, "Charts").QuerySelector(".mud-expand-panel-header")!;
        chartsHeader.Click();
        provider.WaitForAssertion(() =>
        {
            FindPanel(provider, "Charts").ClassList.Should().Contain("mud-panel-expanded");
        });

        cut.Find("button.mud-chip").Click();
        provider.WaitForAssertion(() => provider.FindAll(".mud-expand-panel").Should().BeEmpty());

        cut.Find("button.mud-chip").Click();
        provider.WaitForAssertion(() =>
        {
            FindPanel(provider, "Charts").ClassList.Should().NotContain("mud-panel-expanded");
            FindPanel(provider, "Processing").ClassList.Should().NotContain("mud-panel-expanded");
        });
    }
}
