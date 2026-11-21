using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceRestartTests
{
    private IMqttManagedClient _client = null!;
    private InMemorySettingsDocument _document = null!;
    private ChartSettings _charts = null!;
    private ChartDataService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;
    private Func<Task>? _onSubscribe;
    private Task _subscribeWork = Task.CompletedTask;

    [SetUp]
    public void Setup()
    {
        _client = Substitute.For<IMqttManagedClient>();
        _document = new InMemorySettingsDocument();
        _charts = new ChartSettings(_document);
        _handler = null;
        _onSubscribe = null;
        _subscribeWork = Task.CompletedTask;
        _client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x =>
            {
                _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
                _subscribeWork = _onSubscribe?.Invoke() ?? Task.CompletedTask;
            });
        _client
            .When(x => x.ApplicationMessageReceivedAsync -= Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(_ => _handler = null);
        _service = new ChartDataService(_client, new JsonFieldExtractor(), new ChartFieldRegistry(),
            _charts, TestPipelineHelper.BuildBuiltInPipeline(), Substitute.For<ISparkplugSettings>());
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task SeriesAddedWhileStopped_AcceptsPointsAfterSameConnectionRestart()
    {
        var connectionId = Guid.NewGuid();
        _service.SetConnection(connectionId);
        await _service.StartAsync();
        await _service.StopAsync();

        var series = new ChartSeries { Topic = "restart/added", JsonPath = "value" };
        await _charts.AddChartAsync(connectionId, new ChartConfiguration { MaxPoints = 100, Series = [series] });

        await _service.StartAsync();
        await FireAsync("restart/added", """{"value": 5}""");

        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(5);
    }

    [Test]
    public async Task SourceChangedWhileStopped_ResetsOldPointsAndAcceptsNewSource()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Topic = "restart/changed", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 100, Series = [series] };
        await SeedAsync(connectionId, chart);
        await FireAsync("restart/changed", """{"value": 1}""");
        _service.GetPoints(series.Id).Should().ContainSingle();

        await _service.StopAsync();
        await _charts.UpdateChartAsync(connectionId, ReplaceSeries(chart,
            new ChartSeries { Id = series.Id, Topic = series.Topic, JsonPath = "other" }));
        await _service.StartAsync();

        _service.GetPoints(series.Id).Should().BeEmpty();

        await FireAsync("restart/changed", """{"value": 3}""");
        _service.GetPoints(series.Id).Should().BeEmpty();

        await FireAsync("restart/changed", """{"value": 1, "other": 2}""");
        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Test]
    public async Task UnchangedSeriesRetainsHistoryWhenSiblingChangesWhileStopped()
    {
        var connectionId = Guid.NewGuid();
        var kept = new ChartSeries { Topic = "restart/kept", JsonPath = "value" };
        var changed = new ChartSeries { Topic = "restart/sibling", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 100, Series = [kept, changed] };
        await SeedAsync(connectionId, chart);
        await FireAsync("restart/kept", """{"value": 1}""");
        await FireAsync("restart/sibling", """{"value": 2}""");

        await _service.StopAsync();
        await _charts.UpdateChartAsync(connectionId, ReplaceSeries(chart,
            new ChartSeries { Id = kept.Id, Topic = kept.Topic, JsonPath = kept.JsonPath, DisplayName = "Renamed" },
            new ChartSeries { Id = changed.Id, Topic = changed.Topic, JsonPath = "other" }));
        await _service.StartAsync();

        _service.GetPoints(kept.Id).Should().ContainSingle().Which.Value.Should().Be(1);
        _service.GetPoints(changed.Id).Should().BeEmpty();

        await FireAsync("restart/kept", """{"value": 3}""");
        _service.GetPoints(kept.Id).Select(point => point.Value).Should().Equal(1, 3);
    }

    [Test]
    public async Task RemovedSeriesWhileStopped_ReleasesBufferOnRestart()
    {
        var connectionId = Guid.NewGuid();
        var removed = new ChartSeries { Topic = "restart/removed", JsonPath = "value" };
        var kept = new ChartSeries { Topic = "restart/remaining", JsonPath = "value" };
        var removedChart = new ChartConfiguration { MaxPoints = 100, Series = [removed] };
        var keptChart = new ChartConfiguration { MaxPoints = 100, Series = [kept] };
        await SeedAsync(connectionId, removedChart, keptChart);
        await FireAsync("restart/removed", """{"value": 1}""");
        await FireAsync("restart/remaining", """{"value": 2}""");

        await _service.StopAsync();
        await _charts.RemoveChartAsync(connectionId, removedChart.Id);
        await _service.StartAsync();

        _service.GetPoints(removed.Id).Should().BeEmpty();
        _service.GetPoints(kept.Id).Should().ContainSingle().Which.Value.Should().Be(2);

        await FireAsync("restart/remaining", """{"value": 3}""");
        _service.GetPoints(kept.Id).Select(point => point.Value).Should().Equal(2, 3);
    }

    [Test]
    public async Task ChartAddedAfterSetConnectionBeforeFirstStart_AcceptsPoints()
    {
        var connectionId = Guid.NewGuid();
        _service.SetConnection(connectionId);

        var series = new ChartSeries { Topic = "restart/first", JsonPath = "value" };
        await _charts.AddChartAsync(connectionId, new ChartConfiguration { MaxPoints = 100, Series = [series] });

        await _service.StartAsync();
        await FireAsync("restart/first", """{"value": 4}""");

        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(4);
    }

    [Test]
    public async Task MessageArrivingBeforeStartCompletes_IsAccepted()
    {
        var connectionId = Guid.NewGuid();
        _service.SetConnection(connectionId);
        var series = new ChartSeries { Topic = "restart/early", JsonPath = "value" };
        await _charts.AddChartAsync(connectionId, new ChartConfiguration { MaxPoints = 100, Series = [series] });
        _onSubscribe = () => FireAsync("restart/early", """{"value": 6}""");

        await _service.StartAsync();
        await _subscribeWork;

        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(6);
    }

    [Test]
    public async Task SourceChangedDuringRestart_ResetsHistoryAndAcceptsFreshSource()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Topic = "restart/window", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 100, Series = [series] };
        await SeedAsync(connectionId, chart);
        await FireAsync("restart/window", """{"value": 1}""");
        _service.GetPoints(series.Id).Should().ContainSingle();

        await _service.StopAsync();
        _onSubscribe = () => _charts.UpdateChartAsync(connectionId, ReplaceSeries(chart,
            new ChartSeries { Id = series.Id, Topic = series.Topic, JsonPath = "other" }));

        await _service.StartAsync();
        await _subscribeWork;

        _service.GetPoints(series.Id).Should().BeEmpty();

        await FireAsync("restart/window", """{"value": 1, "other": 2}""");
        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    private async Task SeedAsync(Guid connectionId, params ChartConfiguration[] charts)
    {
        foreach (var chart in charts)
            await _charts.AddChartAsync(connectionId, chart);
        _service.SetConnection(connectionId);
        await _service.StartAsync();
    }

    private static ChartConfiguration ReplaceSeries(ChartConfiguration chart, params ChartSeries[] series) =>
        new() { Id = chart.Id, Name = chart.Name, MaxPoints = chart.MaxPoints, Series = [.. series] };

    private Task FireAsync(string topic, string payload) => _handler!(MakeArgs(topic, payload));

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private sealed class InMemorySettingsDocument : ISettingsDocument
    {
        public AppConfiguration Config { get; } = new();

        public Task MutateAndSaveAsync(Action<AppConfiguration> mutate)
        {
            mutate(Config);
            return Task.CompletedTask;
        }

        public Task ExclusiveAsync(Func<Task> action) => action();

        public Task SaveAsync() => Task.CompletedTask;
    }
}
