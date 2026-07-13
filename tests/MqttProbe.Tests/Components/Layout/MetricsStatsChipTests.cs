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
            AppHealth: new AppHealthMetricsSnapshot(
                Available: appHealthAvailable, CpuUsagePercent: 0, ManagedHeapMb: 0,
                WorkingSetMb: 0, ThreadCount: 0, ThreadPoolQueueLength: 0,
                GcGen2Collections: 0, UptimeSeconds: 0),
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
    public void HidesHealthPanel_WhenAppHealthUnavailable()
    {
        _mockMetrics.GetSnapshot().Returns(CreateSnapshot(appHealthAvailable: false));
        var provider = Render<MudPopoverProvider>();
        var cut = Render<MetricsStatsChip>();

        cut.Find("button.mud-chip").Click();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Diagnostics");
            provider.Markup.Should().NotContain("Health");
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
}
