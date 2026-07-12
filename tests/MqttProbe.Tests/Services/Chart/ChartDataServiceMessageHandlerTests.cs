using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Chart;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceMessageHandlerTests
{
    private IManagedMqttClient _mockClient = null!;
    private ChartFieldRegistry _registry = null!;
    private ISettingsStore _mockSettingsStore = null!;
    private ChartDataService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public async Task Setup()
    {
        _mockClient = Substitute.For<IManagedMqttClient>();
        _registry = new ChartFieldRegistry();
        _mockSettingsStore = Substitute.For<ISettingsStore>();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns([]);
        var mockDecoder = Substitute.For<IPayloadDecoder>();
        mockDecoder.Decode(Arg.Any<MqttApplicationMessageReceivedEventArgs>())
            .Returns(x =>
            {
                var e = (MqttApplicationMessageReceivedEventArgs)x[0]!;
                var seg = e.ApplicationMessage.PayloadSegment;
                var payload = seg.Count > 0 ? System.Text.Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count) : string.Empty;
                return new DecodedPayload(payload, DetectedPayloadFormat.PlainText);
            });
        _service = new ChartDataService(_mockClient, mockDecoder, new JsonFieldExtractor(), _registry, _mockSettingsStore);

        _handler = null;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        await _service.StartAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _mockClient.Dispose();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private Task Fire(string topic, string payload) => _handler!(MakeArgs(topic, payload));

    private static ChartConfiguration ConfigWith(int maxPoints, params ChartSeries[] series) =>
        new() { MaxPoints = maxPoints, Series = [.. series] };

    [Test]
    public async Task MatchingTopicAndPath_AddsPointToBuffer()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "temperature" })
        ]);

        await Fire("sensor/temp", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(21.5, 0.001);
    }

    [Test]
    public async Task NonMatchingTopic_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "temperature" })
        ]);

        await Fire("sensor/humidity", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task NonMatchingJsonPath_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "pressure" })
        ]);

        await Fire("sensor/temp", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task MatchingMessage_OnDataUpdated_Fires()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        var fired = false;
        _service.OnDataUpdated += () => fired = true;

        await Fire("t", """{"v": 1.0}""");

        fired.Should().BeTrue();
    }

    [Test]
    public async Task NoMatchingSeries_OnDataUpdated_DoesNotFire()
    {
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns([]);

        var fired = false;
        _service.OnDataUpdated += () => fired = true;

        await Fire("t", """{"v": 1.0}""");

        fired.Should().BeFalse();
    }

    [Test]
    public async Task BufferExceedsMaxPoints_OldestPointRemoved()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(3, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        for (var i = 1; i <= 5; i++)
            await Fire("t", $$"""{"v": {{i}}.0}""");

        var points = _service.GetPoints(seriesId);
        points.Should().HaveCount(3);
        points.Select(p => p.Value).Should().NotContain(1.0).And.NotContain(2.0);
    }

    [Test]
    public async Task EmptyPayload_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        await Fire("t", "");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task InvalidJson_DoesNotThrow()
    {
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns([]);

        var act = async () => await Fire("t", "not json at all!@#");
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task MatchingMessage_UpdatesFieldRegistry()
    {
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns([]);

        await Fire("sensor/data", """{"temp": 22.0, "humidity": 65.0}""");

        _registry.GetTopics().Should().Contain("sensor/data");
        _registry.GetFields("sensor/data").Should().HaveCount(2);
    }

    [Test]
    public async Task MultipleSeriesOnSameTopic_AllBuffersUpdated()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100,
                new ChartSeries { Id = id1, Topic = "sensor", JsonPath = "temp" },
                new ChartSeries { Id = id2, Topic = "sensor", JsonPath = "humidity" })
        ]);

        await Fire("sensor", """{"temp": 22.0, "humidity": 65.0}""");

        _service.GetPoints(id1).Should().ContainSingle().Which.Value.Should().BeApproximately(22.0, 0.001);
        _service.GetPoints(id2).Should().ContainSingle().Which.Value.Should().BeApproximately(65.0, 0.001);
    }

    [Test]
    public async Task MultipleMessages_PointsAccumulate()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "v" })
        ]);

        await Fire("t", """{"v": 1.0}""");
        await Fire("t", """{"v": 2.0}""");
        await Fire("t", """{"v": 3.0}""");

        _service.GetPoints(seriesId).Should().HaveCount(3);
    }

    [Test]
    public async Task NonNumericJsonField_NoPointAdded()
    {
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "t", JsonPath = "name" })
        ]);

        // "name" is a string value — JsonFieldExtractor only extracts numeric fields
        await Fire("t", """{"name": "sensor-1", "temp": 21.5}""");

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task MessageHandler_WhenRegistryThrows_LogsAndDoesNotPropagate()
    {
        var client = Substitute.For<IManagedMqttClient>();
        var registry = Substitute.For<IChartFieldRegistry>();
        registry.When(x => x.Update(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, ExtractedField>>()))
            .Do(_ => throw new InvalidOperationException("registry failed"));
        var configStore = Substitute.For<ISettingsStore>();
        configStore.GetCharts(Arg.Any<Guid>()).Returns([]);
        var logger = new CapturingLogger<ChartDataService>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        var mockDecoder = Substitute.For<IPayloadDecoder>();
        mockDecoder.Decode(Arg.Any<MqttApplicationMessageReceivedEventArgs>())
            .Returns(x =>
            {
                var e = (MqttApplicationMessageReceivedEventArgs)x[0]!;
                var seg = e.ApplicationMessage.PayloadSegment;
                var payload = seg.Count > 0 ? System.Text.Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count) : string.Empty;
                return new DecodedPayload(payload, DetectedPayloadFormat.PlainText);
            });
        using var service = new ChartDataService(
            client,
            mockDecoder,
            new JsonFieldExtractor(),
            registry,
            configStore,
            logger);
        await service.StartAsync();

        var act = async () => await handler!(MakeArgs("sensor/data", """{"temp":22.0}"""));

        await act.Should().NotThrowAsync();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("Error processing chart data message", StringComparison.Ordinal) &&
            entry.Exception != null);
    }

    [Test]
    public async Task SetConnection_WithMatchingConfig_BuffersPointsForActiveConnection()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(connectionId).Returns(
        [
            ConfigWith(100, new ChartSeries { Id = seriesId, Topic = "sensor/temp", JsonPath = "temperature" })
        ]);

        _service.SetConnection(connectionId);
        await Fire("sensor/temp", """{"temperature": 21.5}""");

        _service.GetPoints(seriesId).Should().ContainSingle()
            .Which.Value.Should().BeApproximately(21.5, 0.001);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

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
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
