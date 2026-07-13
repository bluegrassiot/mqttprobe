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
    public void RendersHealthPanel_WhenAppHealthAvailable()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: true));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Health"));
    }

    [Test]
    public void ShowsHealthAndDiagnostics_WhenAppHealthPartiallyAvailable()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: false));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Health");
            provider.Markup.Should().Contain("Diagnostics");
        });
    }

    [Test]
    public void HidesHealthPanel_WhenAppHealthHasNoValues()
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
            provider.Markup.Should().NotContain("Health");
            provider.Markup.Should().Contain("Diagnostics");
        });
    }

    [Test]
    public void RendersDiagnosticsPanel_WhenAppHealthUnavailable()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: false));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Diagnostics"));
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
            provider.Markup.Should().Contain("Health");
            provider.Markup.Should().Contain("Managed heap");
            provider.Markup.Should().Contain("ThreadPool queue");
            provider.Markup.Should().Contain("GC Gen2 collections");
            provider.Markup.Should().Contain("Uptime");
            provider.Markup.Should().NotContain("CPU usage");
            provider.Markup.Should().NotContain("Working set");
            provider.Markup.Should().NotContain(">Threads<");
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
            provider.Markup.Should().Contain("CPU usage");
            provider.Markup.Should().Contain("Managed heap");
            provider.Markup.Should().Contain("Working set");
            provider.Markup.Should().Contain(">Threads<");
            provider.Markup.Should().Contain("ThreadPool queue");
            provider.Markup.Should().Contain("GC Gen2 collections");
            provider.Markup.Should().Contain("Uptime");
        });
    }
}
