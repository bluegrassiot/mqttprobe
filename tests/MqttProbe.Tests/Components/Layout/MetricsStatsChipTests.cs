using AngleSharp.Dom;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Layout;

[TestFixture]
public class MetricsStatsChipTests : BunitTestContext
{
    private IUxMetricsService _mockMetrics = null!;
    private IManagedMqttClient _mockMqttClient = null!;
    private IMessageStoreManager _mockMsgStore = null!;

    [SetUp]
    public void Setup()
    {
        _mockMetrics = Substitute.For<IUxMetricsService>();
        _mockMqttClient = Substitute.For<IManagedMqttClient>();
        _mockMqttClient.IsConnected.Returns(false);
        _mockMsgStore = Substitute.For<IMessageStoreManager>();

        Services.AddSingleton(_mockMetrics);
        Services.AddSingleton(_mockMqttClient);
        Services.AddSingleton(_mockMsgStore);
    }

    /// <summary>
    /// Finds the .mud-expand-panel whose .mud-expand-panel-text matches <paramref name="title"/>.
    /// Throws if not found or if multiple panels match.
    /// </summary>
    private static IElement FindPanelByTitle(IRenderedComponent<MudPopoverProvider> provider, string title)
    {
        return provider.FindAll(".mud-expand-panel")
            .Single(p => p.QuerySelector(".mud-expand-panel-text")?.TextContent.Trim() == title);
    }

    private static UxMetricsSnapshot CreateSnapshot(bool appHealthAvailable) =>
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
            AppHealth: appHealthAvailable
                ? new AppHealthMetricsSnapshot(
                    CpuUsagePercent: 0, ManagedHeapMb: 0,
                    WorkingSetMb: 0, ThreadCount: 0, ThreadPoolQueueLength: 0,
                    GcGen2Collections: 0, UptimeSeconds: 0)
                : new AppHealthMetricsSnapshot(
                    CpuUsagePercent: null, ManagedHeapMb: 50.0,
                    WorkingSetMb: null, ThreadCount: null, ThreadPoolQueueLength: 3,
                    GcGen2Collections: 10, UptimeSeconds: 3600.0),
            EmulatorPublishersOnline: 0,
            EmulatorPublishCycles: 0, EmulatorNodesInError: 0);

    [Test]
    public void RendersTrafficAndCapacity_WhenFlyoutOpened()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: true));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Traffic");
            provider.Markup.Should().Contain("Capacity");
            provider.Markup.Should().Contain("Displayed messages");
            provider.Markup.Should().Contain("Stored messages");
            provider.Markup.Should().Contain("Topic nodes");
        });
    }

    [Test]
    public void RendersProcessingResourcesAndSystemPanels_WhenFlyoutOpened()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: true));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            var panelTitles = provider.FindAll(".mud-expand-panel-text").Select(e => e.TextContent.Trim()).ToList();
            panelTitles.Should().Contain("Processing");
            panelTitles.Should().Contain("Resources");
            panelTitles.Should().Contain("System");
            panelTitles.Should().NotContain("Health");
            panelTitles.Should().NotContain("Diagnostics");
        });
    }

    [Test]
    public void ResourcesPanel_IncludesResourceRows_WhenAppHealthHasValues()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: true));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            var resources = FindPanelByTitle(provider, "Resources");
            resources.TextContent.Should().Contain("CPU usage");

            var system = FindPanelByTitle(provider, "System");
            system.TextContent.Should().Contain("Connect attempts");
        });
    }

    [Test]
    public void SystemPanel_OmitsHealthRows_WhenAppHealthHasNoValues()
    {
        _mockMetrics.GetSnapshot().Returns(new UxMetricsSnapshot(
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
                CpuUsagePercent: null, ManagedHeapMb: null,
                WorkingSetMb: null, ThreadCount: null, ThreadPoolQueueLength: null,
                GcGen2Collections: null, UptimeSeconds: null),
            EmulatorPublishersOnline: 0,
            EmulatorPublishCycles: 0, EmulatorNodesInError: 0));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            var panelTitles = provider.FindAll(".mud-expand-panel-text").Select(e => e.TextContent.Trim()).ToList();
            panelTitles.Should().NotContain("Resources");
            panelTitles.Should().Contain("System");

            var system = FindPanelByTitle(provider, "System");
            system.TextContent.Should().Contain("Connect attempts");
            system.TextContent.Should().NotContain("CPU usage");
            system.TextContent.Should().NotContain("Managed heap");
            system.TextContent.Should().NotContain("Uptime");
        });
    }

    [Test]
    public void ProcessingPanel_IncludesByFormat_WhenFormatsPresent()
    {
        _mockMetrics.GetSnapshot().Returns(new UxMetricsSnapshot(
            ConnectAttempts: 0, ConnectSuccesses: 0, ConnectFailures: 0,
            PublishSuccesses: 0, PublishFailures: 0,
            ChartsCreated: 0, SeriesAddedToExistingCharts: 0,
            MessagesProcessed: 100, MessagesDropped: 0,
            AvgProcessingTimeUs: 0, MaxProcessingTimeUs: 0,
            AvgPayloadBytes: 0, MaxPayloadBytes: 0,
            CurrentMessagesPerSecond: 0,
            MessageRateHistory: new int[UxMetricsService.RateWindowSeconds],
            MessagesProcessedByFormat: new Dictionary<string, long>
            {
                ["Json"] = 80,
                ["Sparkplug B"] = 20
            },
            ChartFunnelBySource: new Dictionary<string, long>(),
            MaxDisplayMessages: 100, CurrentDisplayedMessageCount: 0,
            AppHealth: new AppHealthMetricsSnapshot(
                CpuUsagePercent: null, ManagedHeapMb: null,
                WorkingSetMb: null, ThreadCount: null, ThreadPoolQueueLength: null,
                GcGen2Collections: null, UptimeSeconds: null),
            EmulatorPublishersOnline: 0,
            EmulatorPublishCycles: 0, EmulatorNodesInError: 0));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            var processing = FindPanelByTitle(provider, "Processing");
            processing.TextContent.Should().Contain("Messages by payload format");
            processing.TextContent.Should().Contain("Json");
            processing.TextContent.Should().Contain("Sparkplug B");
            processing.TextContent.Should().Contain("Messages processed");
        });
    }

    [Test]
    public void RendersOnlyNonNullHealthRows()
    {
        _mockMetrics.GetSnapshot().Returns(new UxMetricsSnapshot(
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
                CpuUsagePercent: null,
                ManagedHeapMb: 50.0,
                WorkingSetMb: null,
                ThreadCount: null,
                ThreadPoolQueueLength: 3,
                GcGen2Collections: 10,
                UptimeSeconds: 3600.0),
            EmulatorPublishersOnline: 0,
            EmulatorPublishCycles: 0, EmulatorNodesInError: 0));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            var resources = FindPanelByTitle(provider, "Resources");
            resources.TextContent.Should().Contain("Managed heap");
            resources.TextContent.Should().Contain("ThreadPool queue");
            resources.TextContent.Should().Contain("GC Gen2 collections");
            resources.TextContent.Should().Contain("Uptime");
            resources.TextContent.Should().NotContain("CPU usage");
            resources.TextContent.Should().NotContain("Working set");
            resources.TextContent.Should().NotContain("Threads");
        });
    }

    [Test]
    public void RendersAllHealthRows_WhenAllAvailable()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: true));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            var resources = FindPanelByTitle(provider, "Resources");
            resources.TextContent.Should().Contain("CPU usage");
            resources.TextContent.Should().Contain("Managed heap");
            resources.TextContent.Should().Contain("Working set");
            resources.TextContent.Should().Contain("Threads");
            resources.TextContent.Should().Contain("ThreadPool queue");
            resources.TextContent.Should().Contain("GC Gen2 collections");
            resources.TextContent.Should().Contain("Uptime");
        });
    }

    [Test]
    public void ChipActivator_WrapsRateText_AndExposesAccessibleLabel()
    {
        _mockMqttClient.IsConnected.Returns(true);
        _mockMetrics.GetSnapshot().Returns(new UxMetricsSnapshot(
            ConnectAttempts: 0, ConnectSuccesses: 0, ConnectFailures: 0,
            PublishSuccesses: 0, PublishFailures: 0,
            ChartsCreated: 0, SeriesAddedToExistingCharts: 0,
            MessagesProcessed: 0, MessagesDropped: 0,
            AvgProcessingTimeUs: 0, MaxProcessingTimeUs: 0,
            AvgPayloadBytes: 0, MaxPayloadBytes: 0,
            CurrentMessagesPerSecond: 1234,
            MessageRateHistory: new int[UxMetricsService.RateWindowSeconds],
            MessagesProcessedByFormat: new Dictionary<string, long>(),
            ChartFunnelBySource: new Dictionary<string, long>(),
            MaxDisplayMessages: 100, CurrentDisplayedMessageCount: 0,
            AppHealth: new AppHealthMetricsSnapshot(
                CpuUsagePercent: null, ManagedHeapMb: null,
                WorkingSetMb: null, ThreadCount: null, ThreadPoolQueueLength: null,
                GcGen2Collections: null, UptimeSeconds: null),
            EmulatorPublishersOnline: 0,
            EmulatorPublishCycles: 0, EmulatorNodesInError: 0));

        var cut = Render<MetricsStatsChip>();
        var chip = cut.Find("button.mud-chip");
        var rate = cut.Find(".metrics-chip-rate");

        rate.TextContent.Should().Contain("1");
        rate.TextContent.Should().Contain("msg/s");
        chip.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        chip.GetAttribute("aria-label")!.Should().Contain("msg/s");
    }

    [Test]
    public void ChipActivator_ShowsDisconnectedRate_WhenNotConnected()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: false));

        var cut = Render<MetricsStatsChip>();
        var chip = cut.Find("button.mud-chip");
        var rate = cut.Find(".metrics-chip-rate");

        rate.TextContent.Should().Contain("msg/s");
        chip.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        chip.GetAttribute("aria-label")!.Should().Contain("msg/s");
    }

    [Test]
    public void ExpansionPanels_CollapseOnMenuReopen_WhenPreviouslyExpanded()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: true));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        // Open flyout
        cut.Find("button.mud-chip").Click();
        provider.WaitForAssertion(() =>
        {
            provider.FindAll(".mud-expand-panel").Should().HaveCount(3);
        });

        // Expand Processing panel via provider.Find (bUnit event dispatch)
        var processingHeader = provider.Find(".mud-expand-panel .mud-expand-panel-header");
        processingHeader.Click();
        provider.WaitForAssertion(() =>
        {
            var processingPanel = FindPanelByTitle(provider, "Processing");
            processingPanel.ClassList.Should().Contain("mud-panel-expanded");
        });

        // Close menu by clicking the chip again
        cut.Find("button.mud-chip").Click();
        provider.WaitForAssertion(() =>
        {
            provider.FindAll(".mud-expand-panel").Should().BeEmpty();
        });

        // Reopen flyout
        cut.Find("button.mud-chip").Click();
        provider.WaitForAssertion(() =>
        {
            var reopenedProcessing = FindPanelByTitle(provider, "Processing");
            reopenedProcessing.ClassList.Should().NotContain("mud-panel-expanded");

            var resources = FindPanelByTitle(provider, "Resources");
            resources.ClassList.Should().NotContain("mud-panel-expanded");

            var system = FindPanelByTitle(provider, "System");
            system.ClassList.Should().NotContain("mud-panel-expanded");
        });
    }
}
