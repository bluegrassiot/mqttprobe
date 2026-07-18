using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Chart;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Shared.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceTests
{
    private IManagedMqttClient _mockClient = null!;
    private IJsonFieldExtractor _extractor = null!;
    private IChartFieldRegistry _registry = null!;
    private ISettingsStore _mockSettingsStore = null!;
    private ChartDataService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IManagedMqttClient>();
        _extractor = new JsonFieldExtractor();
        _registry = new ChartFieldRegistry();
        _mockSettingsStore = Substitute.For<ISettingsStore>();
        _mockSettingsStore.GetCharts(Arg.Any<Guid>()).Returns([]);

        _handler = null;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        _service = new ChartDataService(_mockClient, _extractor, _registry, _mockSettingsStore,
            TestPipelineHelper.BuildBuiltInPipeline());
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _mockClient.Dispose();
    }

    [Test]
    public async Task StartAsync_SetsIsListeningTrue_AndSubscribesToEvent()
    {
        await _service.StartAsync();
        _service.IsListening.Should().BeTrue();
        _mockClient.Received(1).ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task StopAsync_SetsIsListeningFalse_AndUnsubscribes()
    {
        await _service.StartAsync();
        await _service.StopAsync();
        _service.IsListening.Should().BeFalse();
        _mockClient.Received(1).ApplicationMessageReceivedAsync -= Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public void GetPoints_UnknownSeriesId_ReturnsEmpty()
    {
        var points = _service.GetPoints(Guid.NewGuid());
        points.Should().BeEmpty();
    }

    [Test]
    public async Task StartAsync_Idempotent_DoesNotSubscribeTwice()
    {
        await _service.StartAsync();
        await _service.StartAsync();
        _mockClient.Received(1).ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public void OnDataUpdated_IsNullByDefault()
    {
        Action? captured = null;
        _service.OnDataUpdated += () => captured = () => { };
        captured.Should().BeNull();
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ChartConfiguration_MaxPoints_FallsBackToDefault_WhenNonPositive(int maxPoints)
    {
        var config = new ChartConfiguration { MaxPoints = maxPoints };

        config.MaxPoints.Should().Be(500);
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ChartConfiguration_TimeWindowMinutes_NormalizesNonPositiveToNull(int value)
    {
        var config = new ChartConfiguration { TimeWindowMinutes = value };

        config.TimeWindowMinutes.Should().BeNull();
    }

    [Test]
    public void ChartConfiguration_TimeWindowMinutes_PreservesPositiveValue()
    {
        var config = new ChartConfiguration { TimeWindowMinutes = 15 };

        config.TimeWindowMinutes.Should().Be(15);
    }

    [Test]
    public void ChartConfiguration_TimeWindowMinutes_NullStaysNull()
    {
        var config = new ChartConfiguration { TimeWindowMinutes = null };

        config.TimeWindowMinutes.Should().BeNull();
    }

    [Test]
    public async Task StartAsync_ConcurrentCalls_RegistersHandlerExactlyOnce()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _service.StartAsync()));

        await Task.WhenAll(tasks);

        _mockClient.Received(1).ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }

    [Test]
    public async Task SetConnection_ClearsDataBuffers()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(connectionId).Returns([
            new ChartConfiguration
            {
                Series = [new ChartSeries { Id = seriesId, Topic = "test/topic", JsonPath = "value" }],
                MaxPoints = 100
            }
        ]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();

        await _handler!(MakeArgs("test/topic", """{"value": 42}"""));

        _service.GetPoints(seriesId).Should().NotBeEmpty();

        _service.SetConnection(Guid.NewGuid());

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public async Task OnChartsChanged_IgnoresEventForNonMatchingConnectionId()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(connectionId).Returns([
            new ChartConfiguration
            {
                Series = [new ChartSeries { Id = seriesId, Topic = "test/topic", JsonPath = "value" }],
                MaxPoints = 100
            }
        ]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();

        await _handler!(MakeArgs("test/topic", """{"value": 42}"""));

        _service.GetPoints(seriesId).Should().NotBeEmpty();

        _mockSettingsStore.ChartsChanged += Raise.Event<Action<Guid>>(Guid.NewGuid());

        _service.GetPoints(seriesId).Should().NotBeEmpty();
    }

    [Test]
    public async Task OnChartsChanged_ClearsBuffersAndFiresOnDataUpdatedForMatchingConnectionId()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockSettingsStore.GetCharts(connectionId).Returns([
            new ChartConfiguration
            {
                Series = [new ChartSeries { Id = seriesId, Topic = "test/topic", JsonPath = "value" }],
                MaxPoints = 100
            }
        ]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();

        await _handler!(MakeArgs("test/topic", """{"value": 42}"""));

        _service.GetPoints(seriesId).Should().NotBeEmpty();

        var onUpdated = false;
        _service.OnDataUpdated += () => onUpdated = true;

        _mockSettingsStore.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(seriesId).Should().BeEmpty();
        onUpdated.Should().BeTrue();
    }

    [Test]
    public async Task ClearBuffers_WithPopulatedBuffers_ClearsAllSeriesData()
    {
        var connId = Guid.NewGuid();
        _service.SetConnection(connId);

        var seriesId = Guid.NewGuid();
        var config = new ChartConfiguration
        {
            MaxPoints = 100,
            Series = [new ChartSeries { Id = seriesId, Topic = "data/temp", JsonPath = "value" }]
        };
        _mockSettingsStore.GetCharts(connId).Returns([config]);

        await _service.StartAsync();

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic("data/temp")
            .WithPayload("""{"value":42}""")
            .Build();
        var args = new MqttApplicationMessageReceivedEventArgs("test", msg,
            new MQTTnet.Packets.MqttPublishPacket(), null);
        await _handler!(args);

        _service.GetPoints(seriesId).Should().NotBeEmpty();

        _service.ClearBuffers();

        _service.GetPoints(seriesId).Should().BeEmpty();
    }

    [Test]
    public void ClearBuffers_WhenEmpty_DoesNotThrow()
    {
        var act = () => _service.ClearBuffers();

        act.Should().NotThrow();
    }

    [Test]
    public void ClearBuffers_FiresOnDataUpdated()
    {
        var fired = false;
        _service.OnDataUpdated += () => fired = true;

        _service.ClearBuffers();

        fired.Should().BeTrue();
    }

    [Test]
    public async Task ClearBuffers_DoesNotChangeConnectionId()
    {
        var connId = Guid.NewGuid();
        _service.SetConnection(connId);

        await _service.StartAsync();

        _service.ClearBuffers();

        _mockSettingsStore.ClearReceivedCalls();
        _mockSettingsStore.GetCharts(connId).Returns([]);

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic("data/temp")
            .WithPayload("""{"value":1}""")
            .Build();
        var args = new MqttApplicationMessageReceivedEventArgs("test", msg,
            new MQTTnet.Packets.MqttPublishPacket(), null);
        await _handler!(args);

        _mockSettingsStore.Received(1).GetCharts(connId);
    }

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }
}
