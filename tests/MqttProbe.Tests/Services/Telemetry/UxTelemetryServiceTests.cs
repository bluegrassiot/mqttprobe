using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Services.Telemetry;

[TestFixture]
public class UxTelemetryServiceTests
{
    private CapturingLogger<UxTelemetryService> _logger = null!;
    private UxTelemetryService _service = null!;

    [SetUp]
    public void Setup()
    {
        _logger = new CapturingLogger<UxTelemetryService>();
        _service = new UxTelemetryService(_logger);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
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
    public void RecordMessageProcessed_UsesOnlyBoundedMetricTags()
    {
        var measurements = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "MqttProbe" &&
                instrument.Name == "mqttprobe.messages.processed")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        listener.Start();

        using var service = new UxTelemetryService(_logger);
        service.RecordMessageProcessed("Json");

        measurements.Should().ContainSingle();
        measurements.Single().Should().Contain(tag => tag.Key == "format" && tag.Value != null && tag.Value.ToString() == "Json");
        measurements.Single().Should().NotContain(tag => tag.Key == "topic_root");
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
