using Microsoft.Extensions.Logging;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;

namespace MqttProbe.Shared.Tests.Services.Metrics;

[TestFixture]
public class UxMetricsServiceTests
{
    private CapturingLogger<UxMetricsService> _logger = null!;
    private ISettingsStore _mockSettings = null!;
    private IAppHealthMetricsCollector _mockHealthCollector = null!;
    private UxMetricsService _service = null!;

    [SetUp]
    public void Setup()
    {
        _logger = new CapturingLogger<UxMetricsService>();
        _mockSettings = Substitute.For<ISettingsStore>();
        _mockSettings.Config.Returns(new AppConfiguration());
        _mockHealthCollector = Substitute.For<IAppHealthMetricsCollector>();
        _mockHealthCollector.GetSnapshot().Returns(new AppHealthMetricsSnapshot(
            CpuUsagePercent: 1.0, ManagedHeapMb: 2.0,
            WorkingSetMb: 3.0, ThreadCount: 4, ThreadPoolQueueLength: 5,
            GcGen2Collections: 6, UptimeSeconds: 7.0));
        _service = new UxMetricsService(_logger, _mockSettings, _mockHealthCollector);
    }

    [TearDown]
    public void TearDown()
    {
        _mockHealthCollector.Dispose();
    }

    [Test]
    public void RecordPublishOutcome_RecordsCountersWithoutInformationOrWarningLogNoise()
    {
        _service.RecordPublishOutcome(true);
        _service.RecordPublishOutcome(false);

        var snapshot = _service.GetSnapshot();
        snapshot.PublishSuccesses.Should().Be(1);
        snapshot.PublishFailures.Should().Be(1);
        _logger.Entries.Where(entry => entry.Message.Contains("publish", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(entry => entry.Level == LogLevel.Debug);
    }

    [Test]
    public void RecordMessageProcessed_TracksFormatInSnapshot()
    {
        _service.RecordMessageProcessed("Json");

        var snapshot = _service.GetSnapshot();
        snapshot.MessagesProcessed.Should().Be(1);
        snapshot.MessagesProcessedByFormat.Should().ContainKey("Json").WhoseValue.Should().Be(1);
    }

    [Test]
    public void GetSnapshot_DefaultDisplayedCount_IsZero()
    {
        var snapshot = _service.GetSnapshot();
        snapshot.CurrentDisplayedMessageCount.Should().Be(0);
    }

    [Test]
    public void GetSnapshot_AfterSetDisplayedMessageCount_ReturnsCount()
    {
        _service.SetDisplayedMessageCount(42);
        var snapshot = _service.GetSnapshot();
        snapshot.CurrentDisplayedMessageCount.Should().Be(42);
    }

    [Test]
    public void GetSnapshot_ReadsMaxDisplayMessagesFromSettings()
    {
        _mockSettings.Config.Performance.MaxDisplayMessages = 300;
        var snapshot = _service.GetSnapshot();
        snapshot.MaxDisplayMessages.Should().Be(300);
    }

    [Test]
    public void GetSnapshot_ReflectsSettingsChange()
    {
        _mockSettings.Config.Performance.MaxDisplayMessages = 100;
        var s1 = _service.GetSnapshot();
        s1.MaxDisplayMessages.Should().Be(100);

        _mockSettings.Config.Performance.MaxDisplayMessages = 200;
        var s2 = _service.GetSnapshot();
        s2.MaxDisplayMessages.Should().Be(200);
    }

    [Test]
    public void GetSnapshot_DefaultEmulationHealth_IsZero()
    {
        var snapshot = _service.GetSnapshot();
        snapshot.EmulatorPublishersOnline.Should().Be(0);
        snapshot.EmulatorPublishCycles.Should().Be(0);
        snapshot.EmulatorNodesInError.Should().Be(0);
    }

    [Test]
    public void GetSnapshot_DefaultAppHealth_DelegatesToCollector()
    {
        var snapshot = _service.GetSnapshot();

        snapshot.AppHealth.HasAny.Should().BeTrue();
        snapshot.AppHealth.CpuUsagePercent.Should().Be(1.0);
        snapshot.AppHealth.ManagedHeapMb.Should().Be(2.0);
        snapshot.AppHealth.WorkingSetMb.Should().Be(3.0);
        snapshot.AppHealth.ThreadCount.Should().Be(4);
        snapshot.AppHealth.ThreadPoolQueueLength.Should().Be(5);
        snapshot.AppHealth.GcGen2Collections.Should().Be(6);
        snapshot.AppHealth.UptimeSeconds.Should().Be(7.0);
    }

    [Test]
    public void GetSnapshot_AfterUpdateEmulatorHealth_ReturnsEmulationFields()
    {
        _service.UpdateEmulatorHealth(publishersOnline: 2, publishCycles: 100, nodesInError: 1);
        var snapshot = _service.GetSnapshot();

        snapshot.EmulatorPublishersOnline.Should().Be(2);
        snapshot.EmulatorPublishCycles.Should().Be(100);
        snapshot.EmulatorNodesInError.Should().Be(1);
    }

    [Test]
    public void GetSnapshot_AfterClearEmulatorHealth_ReturnsZero()
    {
        _service.UpdateEmulatorHealth(publishersOnline: 3, publishCycles: 50, nodesInError: 2);
        _service.ClearEmulatorHealth();

        var snapshot = _service.GetSnapshot();
        snapshot.EmulatorPublishersOnline.Should().Be(0);
        snapshot.EmulatorPublishCycles.Should().Be(0);
        snapshot.EmulatorNodesInError.Should().Be(0);
    }

    [Test]
    public void UpdateEmulatorHealth_StoresAllThreeValues()
    {
        _service.UpdateEmulatorHealth(publishersOnline: 5, publishCycles: 999, nodesInError: 0);
        var snapshot = _service.GetSnapshot();

        snapshot.EmulatorPublishersOnline.Should().Be(5);
        snapshot.EmulatorPublishCycles.Should().Be(999);
        snapshot.EmulatorNodesInError.Should().Be(0);
    }

    [Test]
    public void ClearEmulatorHealth_DoesNotAffectAppHealth()
    {
        _service.UpdateEmulatorHealth(publishersOnline: 5, publishCycles: 999, nodesInError: 2);
        _service.ClearEmulatorHealth();

        var snapshot = _service.GetSnapshot();
        // App health is delegated to collector, unaffected by emulation clear
        snapshot.AppHealth.HasAny.Should().BeTrue();
        snapshot.AppHealth.ManagedHeapMb.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void GetSnapshot_WhenCollectorUnavailable_SetsAppHealthUnavailable()
    {
        _mockHealthCollector.GetSnapshot().Returns(new AppHealthMetricsSnapshot(
            CpuUsagePercent: null, ManagedHeapMb: 2.0,
            WorkingSetMb: null, ThreadCount: null, ThreadPoolQueueLength: 5,
            GcGen2Collections: 6, UptimeSeconds: 7.0));

        var snapshot = _service.GetSnapshot();

        snapshot.AppHealth.HasAny.Should().BeTrue();
        snapshot.AppHealth.CpuUsagePercent.Should().BeNull();
        snapshot.AppHealth.ManagedHeapMb.Should().Be(2.0);
        snapshot.AppHealth.WorkingSetMb.Should().BeNull();
        snapshot.AppHealth.ThreadCount.Should().BeNull();
        snapshot.AppHealth.ThreadPoolQueueLength.Should().Be(5);
        snapshot.AppHealth.GcGen2Collections.Should().Be(6);
        snapshot.AppHealth.UptimeSeconds.Should().Be(7.0);
    }

    [Test]
    public void GetSnapshot_WhenCollectorAvailable_SetsAppHealthAvailable()
    {
        var snapshot = _service.GetSnapshot();

        snapshot.AppHealth.HasAny.Should().BeTrue();
    }

    [Test]
    public void GetSnapshot_WhenCollectorUnavailable_PreservesNonHealthMetrics()
    {
        _mockHealthCollector.GetSnapshot().Returns(new AppHealthMetricsSnapshot(
            CpuUsagePercent: null, ManagedHeapMb: 2.0,
            WorkingSetMb: null, ThreadCount: null, ThreadPoolQueueLength: 5,
            GcGen2Collections: 6, UptimeSeconds: 7.0));

        _service.RecordConnectAttempt();
        _service.RecordPublishOutcome(true);
        _service.RecordMessageProcessed("Json");
        _service.UpdateEmulatorHealth(2, 100, 1);

        var snapshot = _service.GetSnapshot();

        snapshot.ConnectAttempts.Should().Be(1);
        snapshot.PublishSuccesses.Should().Be(1);
        snapshot.MessagesProcessed.Should().Be(1);
        snapshot.MessagesProcessedByFormat.Should().ContainKey("Json");
        snapshot.EmulatorPublishersOnline.Should().Be(2);
        snapshot.AppHealth.HasAny.Should().BeTrue();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
