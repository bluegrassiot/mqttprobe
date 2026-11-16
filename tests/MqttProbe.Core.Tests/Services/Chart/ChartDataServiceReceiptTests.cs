using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceReceiptTests
{
    private IMqttManagedClient _client = null!;
    private IChartSettings _settings = null!;
    private ChartDataService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;
    private ChartSeries _series = null!;
    private Guid _connectionId;

    [SetUp]
    public async Task Setup()
    {
        _connectionId = Guid.NewGuid();
        _series = new ChartSeries { Topic = "receipt/temp", JsonPath = "value" };
        _client = Substitute.For<IMqttManagedClient>();
        _settings = Substitute.For<IChartSettings>();
        _settings.GetCharts(_connectionId).Returns([Config(100, _series)]);
        _handler = null;
        _client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        _service = new ChartDataService(_client, new JsonFieldExtractor(), new ChartFieldRegistry(),
            _settings, TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>());
        _service.SetConnection(_connectionId);
        await _service.StartAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task NewestMessage_TracksTheLastBufferedPointTimestamp()
    {
        await FireAsync("receipt/temp", """{"value": 1}""");
        var first = _service.GetPoints(_series.Id).Single().Timestamp;
        _service.GetLastPointTimestamp(_series.Id).Should().Be(first);

        await FireAsync("receipt/temp", """{"value": 2}""");
        var points = _service.GetPoints(_series.Id);

        points[^1].Value.Should().Be(2);
        _service.GetLastPointTimestamp(_series.Id).Should().Be(points[^1].Timestamp);
    }

    [Test]
    public async Task UnmatchedTopic_LeavesTheTimestampNull()
    {
        await FireAsync("receipt/other", """{"value": 1}""");

        _service.GetLastPointTimestamp(_series.Id).Should().BeNull();
        _service.GetLastPointTimestamp(Guid.NewGuid()).Should().BeNull();
    }

    [Test]
    public async Task TrimmedBuffer_KeepsTheNewestPointTimestamp()
    {
        _settings.GetCharts(_connectionId).Returns([Config(2, _series)]);
        _settings.ChartsChanged += Raise.Event<Action<Guid>>(_connectionId);

        await FireAsync("receipt/temp", """{"value": 1}""");
        await FireAsync("receipt/temp", """{"value": 2}""");
        await FireAsync("receipt/temp", """{"value": 3}""");

        var points = _service.GetPoints(_series.Id);
        points.Select(point => point.Value).Should().Equal(2d, 3d);
        _service.GetLastPointTimestamp(_series.Id).Should().Be(points[^1].Timestamp);
    }

    [Test]
    public async Task LoweredMaxPoints_KeepsTheNewestPointTimestamp()
    {
        await FireAsync("receipt/temp", """{"value": 1}""");
        await FireAsync("receipt/temp", """{"value": 2}""");
        var newest = _service.GetPoints(_series.Id)[^1].Timestamp;

        _settings.GetCharts(_connectionId).Returns([Config(1, _series)]);
        _settings.ChartsChanged += Raise.Event<Action<Guid>>(_connectionId);

        _service.GetPoints(_series.Id).Should().ContainSingle().Which.Value.Should().Be(2);
        _service.GetLastPointTimestamp(_series.Id).Should().Be(newest);
    }

    [Test]
    public async Task ClearBuffers_ClearsTheTimestamp()
    {
        await FireAsync("receipt/temp", """{"value": 1}""");
        _service.GetLastPointTimestamp(_series.Id).Should().NotBeNull();

        _service.ClearBuffers();

        _service.GetLastPointTimestamp(_series.Id).Should().BeNull();
    }

    [Test]
    public async Task RemovedSeries_ClearsTheTimestamp()
    {
        await FireAsync("receipt/temp", """{"value": 1}""");
        _service.GetLastPointTimestamp(_series.Id).Should().NotBeNull();

        _settings.GetCharts(_connectionId).Returns([]);
        _settings.ChartsChanged += Raise.Event<Action<Guid>>(_connectionId);

        _service.GetLastPointTimestamp(_series.Id).Should().BeNull();
    }

    [Test]
    public async Task ExcludedTopicPurge_ClearsTheTimestamp()
    {
        var series = new ChartSeries { Topic = "sensors/temp", JsonPath = "value" };
        var settings = Substitute.For<IChartSettings>();
        settings.GetCharts(_connectionId).Returns([Config(100, series)]);
        var session = new SessionState { SelectedConnection = new Connection() };
        using var excludes = new TopicExcludeService(
            Substitute.For<ILogger<TopicExcludeService>>(),
            Substitute.For<IConnectionSettings>(), session);

        var client = Substitute.For<IMqttManagedClient>();
        Func<MqttApplicationMessageReceivedEventArgs, Task>? handler = null;
        client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());

        using var service = new ChartDataService(client, new JsonFieldExtractor(), new ChartFieldRegistry(),
            settings, TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>(),
            topicExcludeService: excludes);
        service.SetConnection(_connectionId);
        await service.StartAsync();
        await handler!(MakeArgs("sensors/temp", """{"value": 1}"""));
        service.GetLastPointTimestamp(series.Id).Should().NotBeNull();

        (await excludes.Add("sensors/#").WaitAsync(TimeSpan.FromSeconds(10))).IsValid.Should().BeTrue();

        service.GetPoints(series.Id).Should().BeEmpty();
        service.GetLastPointTimestamp(series.Id).Should().BeNull();
    }

    private Task FireAsync(string topic, string payload) => _handler!(MakeArgs(topic, payload));

    private static ChartConfiguration Config(int maxPoints, params ChartSeries[] series) =>
        new() { MaxPoints = maxPoints, Series = [.. series] };

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }
}
