using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceTests
{
    private IMqttManagedClient _mockClient = null!;
    private IJsonFieldExtractor _extractor = null!;
    private IChartFieldRegistry _registry = null!;
    private IChartSettings _mockChartSettings = null!;
    private ChartDataService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
        _extractor = new JsonFieldExtractor();
        _registry = new ChartFieldRegistry();
        _mockChartSettings = Substitute.For<IChartSettings>();
        _mockChartSettings.GetCharts(Arg.Any<Guid>()).Returns([]);

        _handler = null;
        _mockClient
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        _service = new ChartDataService(_mockClient, _extractor, _registry, _mockChartSettings,
            TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>());
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
        _mockChartSettings.GetCharts(connectionId).Returns([
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
    public async Task SetConnection_SameConnectionPreservesCollectedPoints_AndSwitchingConnectionsClearsThem()
    {
        var connectionA = Guid.NewGuid();
        var connectionB = Guid.NewGuid();
        var seriesA = Guid.NewGuid();
        var seriesB = Guid.NewGuid();
        _mockChartSettings.GetCharts(connectionA).Returns([
            new ChartConfiguration
            {
                Series = [new ChartSeries { Id = seriesA, Topic = "test/topic", JsonPath = "value" }],
                MaxPoints = 100
            }
        ]);
        _mockChartSettings.GetCharts(connectionB).Returns([
            new ChartConfiguration
            {
                Series = [new ChartSeries { Id = seriesB, Topic = "test/topic", JsonPath = "value" }],
                MaxPoints = 100
            }
        ]);

        _service.SetConnection(connectionA);
        await _service.StartAsync();
        await _handler!(MakeArgs("test/topic", """{"value":1}"""));

        _service.SetConnection(connectionA);

        _service.GetPoints(seriesA).Should().ContainSingle().Which.Value.Should().Be(1);
        await _handler!(MakeArgs("test/topic", """{"value":2}"""));
        _service.GetPoints(seriesA).Select(point => point.Value).Should().Equal(1, 2);

        _service.SetConnection(connectionB);
        _service.GetPoints(seriesA).Should().BeEmpty();
        await _handler!(MakeArgs("test/topic", """{"value":3}"""));
        _service.GetPoints(seriesA).Should().BeEmpty();
        _service.GetPoints(seriesB).Should().ContainSingle().Which.Value.Should().Be(3);

        _service.SetConnection(connectionA);
        _service.GetPoints(seriesA).Should().BeEmpty();
        await _handler!(MakeArgs("test/topic", """{"value":4}"""));
        _service.GetPoints(seriesA).Should().ContainSingle().Which.Value.Should().Be(4);
    }

    [Test]
    public async Task OnChartsChanged_IgnoresEventForNonMatchingConnectionId()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(connectionId).Returns([
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

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(Guid.NewGuid());

        _service.GetPoints(seriesId).Should().NotBeEmpty();
    }

    [Test]
    public async Task OnChartsChanged_UnchangedSeries_PreservesPointsAndFiresOnDataUpdated()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        _mockChartSettings.GetCharts(connectionId).Returns([
            new ChartConfiguration
            {
                Series = [new ChartSeries { Id = seriesId, Topic = "test/topic", JsonPath = "value" }],
                MaxPoints = 100
            }
        ]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();

        await _handler!(MakeArgs("test/topic", """{"value": 42}"""));

        _service.GetPoints(seriesId).Should().ContainSingle();

        var onUpdated = false;
        _service.OnDataUpdated += () => onUpdated = true;

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(seriesId).Should().ContainSingle().Which.Value.Should().Be(42);
        onUpdated.Should().BeTrue();
    }

    [Test]
    public async Task OnChartsChanged_WhenSeriesRemoved_DropsOnlyRemovedSeriesPoints()
    {
        var connectionId = Guid.NewGuid();
        var keptSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/kept", JsonPath = "value" };
        var removedSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/removed", JsonPath = "value" };
        _mockChartSettings.GetCharts(connectionId).Returns([ConfigWith(keptSeries, removedSeries)]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();
        await _handler!(MakeArgs("test/kept", """{"value": 1}"""));
        await _handler!(MakeArgs("test/removed", """{"value": 2}"""));

        _mockChartSettings.GetCharts(connectionId).Returns([ConfigWith(keptSeries)]);

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(keptSeries.Id).Should().ContainSingle().Which.Value.Should().Be(1);
        _service.GetPoints(removedSeries.Id).Should().BeEmpty();
    }

    [Test]
    public async Task OnChartsChanged_WhenSeriesJsonPathChangesInPlace_ResetsOnlyChangedSeries()
    {
        var connectionId = Guid.NewGuid();
        var changedSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/topic", JsonPath = "value" };
        var untouchedSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/topic", JsonPath = "other" };
        _mockChartSettings.GetCharts(connectionId).Returns([ConfigWith(changedSeries, untouchedSeries)]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();
        await _handler!(MakeArgs("test/topic", """{"value": 1, "other": 2}"""));

        changedSeries.JsonPath = "other";

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(changedSeries.Id).Should().BeEmpty();
        _service.GetPoints(untouchedSeries.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Test]
    public async Task OnChartsChanged_WhenSeriesTopicChangesInPlace_ResetsOnlyChangedSeries()
    {
        var connectionId = Guid.NewGuid();
        var changedSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/topic", JsonPath = "value" };
        var untouchedSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/other", JsonPath = "value" };
        _mockChartSettings.GetCharts(connectionId).Returns([ConfigWith(changedSeries, untouchedSeries)]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();
        await _handler!(MakeArgs("test/topic", """{"value": 1}"""));
        await _handler!(MakeArgs("test/other", """{"value": 2}"""));

        changedSeries.Topic = "test/moved";

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(changedSeries.Id).Should().BeEmpty();
        _service.GetPoints(untouchedSeries.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Test]
    public async Task OnChartsChanged_WhenSeriesAdded_PreservesExistingPointsAndStartsNewSeriesEmpty()
    {
        var connectionId = Guid.NewGuid();
        var existingSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/topic", JsonPath = "value" };
        var addedSeries = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/added", JsonPath = "value" };
        _mockChartSettings.GetCharts(connectionId).Returns([ConfigWith(existingSeries)]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();
        await _handler!(MakeArgs("test/topic", """{"value": 1}"""));

        _mockChartSettings.GetCharts(connectionId)
            .Returns([ConfigWith(existingSeries), ConfigWith(addedSeries)]);

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(existingSeries.Id).Should().ContainSingle().Which.Value.Should().Be(1);
        _service.GetPoints(addedSeries.Id).Should().BeEmpty();

        await _handler!(MakeArgs("test/added", """{"value": 2}"""));

        _service.GetPoints(existingSeries.Id).Should().ContainSingle().Which.Value.Should().Be(1);
        _service.GetPoints(addedSeries.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Test]
    public async Task OnChartsChanged_WhenOnlyPresentationChanges_PreservesPoints()
    {
        var connectionId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var chart = new ChartConfiguration
        {
            Name = "Before",
            Type = ChartType.Line,
            MaxPoints = 100,
            TimeWindowMinutes = 5,
            Series = [new ChartSeries { Id = seriesId, Topic = "test/topic", JsonPath = "value" }]
        };
        _mockChartSettings.GetCharts(connectionId).Returns([chart]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();
        await _handler!(MakeArgs("test/topic", """{"value": 42}"""));

        chart.Name = "After";
        chart.Type = ChartType.Area;
        chart.MaxPoints = 250;
        chart.TimeWindowMinutes = 30;
        chart.Series[0].DisplayName = "Renamed";
        chart.Series[0].Color = "#000000";

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(seriesId).Should().ContainSingle().Which.Value.Should().Be(42);
    }

    [Test]
    public async Task OnChartsChanged_WhenSeriesMovesToAnotherChart_PreservesPoints()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Id = Guid.NewGuid(), Topic = "test/topic", JsonPath = "value" };
        var firstChart = ConfigWith(series);
        _mockChartSettings.GetCharts(connectionId).Returns([firstChart]);

        _service.SetConnection(connectionId);
        await _service.StartAsync();
        await _handler!(MakeArgs("test/topic", """{"value": 42}"""));

        firstChart.Series.Clear();
        _mockChartSettings.GetCharts(connectionId).Returns([firstChart, ConfigWith(series)]);

        _mockChartSettings.ChartsChanged += Raise.Event<Action<Guid>>(connectionId);

        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(42);
    }

    [Test]
    public async Task ClearBuffers_WithPopulatedBuffers_ClearsAllSeriesData()
    {
        var connId = Guid.NewGuid();

        var seriesId = Guid.NewGuid();
        var config = new ChartConfiguration
        {
            MaxPoints = 100,
            Series = [new ChartSeries { Id = seriesId, Topic = "data/temp", JsonPath = "value" }]
        };
        _mockChartSettings.GetCharts(connId).Returns([config]);
        _service.SetConnection(connId);

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

        _mockChartSettings.ClearReceivedCalls();
        _mockChartSettings.GetCharts(connId).Returns([]);

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic("data/temp")
            .WithPayload("""{"value":1}""")
            .Build();
        var args = new MqttApplicationMessageReceivedEventArgs("test", msg,
            new MQTTnet.Packets.MqttPublishPacket(), null);
        await _handler!(args);

        _mockChartSettings.Received(1).GetCharts(connId);
    }

    private static ChartConfiguration ConfigWith(params ChartSeries[] series) =>
        new() { MaxPoints = 100, Series = [.. series] };

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }
}
