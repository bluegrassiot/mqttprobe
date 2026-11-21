using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServicePointLimitTests
{
    private IMqttManagedClient _client = null!;
    private ParkableSettingsDocument _document = null!;
    private ChartSettings _charts = null!;
    private ChartDataService _service = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        _client = Substitute.For<IMqttManagedClient>();
        _document = new ParkableSettingsDocument(new AppConfiguration());
        _charts = new ChartSettings(_document);
        _handler = null;
        _client
            .When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());
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
    public async Task LoweringMaxPoints_TrimsRetainedPointsToNewestWithoutNewData()
    {
        var connectionId = Guid.NewGuid();
        var target = new ChartSeries { Topic = "limit/target", JsonPath = "value" };
        var unrelated = new ChartSeries { Topic = "limit/unrelated", JsonPath = "value" };
        var targetChart = new ChartConfiguration { MaxPoints = 20, Series = [target] };
        var unrelatedChart = new ChartConfiguration { MaxPoints = 20, Series = [unrelated] };
        await SeedAsync(connectionId, targetChart, unrelatedChart);
        await SeedPointsAsync("limit/target", [.. Enumerable.Range(1, 20)]);
        await SeedPointsAsync("limit/unrelated", 1, 2);

        await _charts.UpdateChartAsync(connectionId, ReplaceChart(targetChart, maxPoints: 10));

        _service.GetPoints(target.Id).Select(point => point.Value)
            .Should().Equal(11d, 12d, 13d, 14d, 15d, 16d, 17d, 18d, 19d, 20d);
        _service.GetPoints(unrelated.Id).Select(point => point.Value).Should().Equal(1d, 2d);
    }

    [Test]
    public async Task RenamingAndRaisingMaxPoints_PreservesRetainedPoints()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Topic = "limit/raised", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 20, Series = [series] };
        await SeedAsync(connectionId, chart);
        await SeedPointsAsync("limit/raised", 1, 2, 3);

        await _charts.UpdateChartAsync(connectionId, ReplaceChart(chart, maxPoints: 50, name: "Renamed"));

        _service.GetPoints(series.Id).Select(point => point.Value).Should().Equal(1d, 2d, 3d);
    }

    [Test]
    public async Task LoweringMaxPointsWhileMessageInFlight_DoesNotRestoreTrimmedPoints()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Topic = "limit/inflight", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 20, Series = [series] };
        await SeedAsync(connectionId, chart);
        await SeedPointsAsync("limit/inflight", [.. Enumerable.Range(1, 20)]);

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("limit/inflight", """{"value": 21}"""));
        await _document.WaitParkedAsync();
        try
        {
            await _charts.UpdateChartAsync(connectionId, ReplaceChart(chart, maxPoints: 10));
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(series.Id).Select(point => point.Value)
            .Should().Equal(12d, 13d, 14d, 15d, 16d, 17d, 18d, 19d, 20d, 21d);
    }

    private async Task SeedAsync(Guid connectionId, params ChartConfiguration[] charts)
    {
        foreach (var chart in charts)
            await _charts.AddChartAsync(connectionId, chart);
        _service.SetConnection(connectionId);
        await _service.StartAsync();
    }

    private async Task SeedPointsAsync(string topic, params int[] values)
    {
        foreach (var value in values)
            await FireAsync(topic, $$"""{"value": {{value}}}""");
    }

    private Task FireAsync(string topic, string payload) => _handler!(MakeArgs(topic, payload));

    private static ChartConfiguration ReplaceChart(ChartConfiguration chart, int maxPoints, string? name = null) =>
        new() { Id = chart.Id, Name = name ?? chart.Name, MaxPoints = maxPoints, Series = [.. chart.Series] };

    private static MqttApplicationMessageReceivedEventArgs MakeArgs(string topic, string payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = topic };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private sealed class ParkableSettingsDocument(AppConfiguration config) : ISettingsDocument
    {
        private static readonly TimeSpan _parkTimeout = TimeSpan.FromSeconds(10);
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public AppConfiguration Config
        {
            get
            {
                if (Interlocked.Exchange(ref _armed, 0) == 0)
                    return config;

                var snapshot = Snapshot(config);
                _parked.TrySetResult();
                if (!_release.Task.Wait(_parkTimeout))
                    throw new TimeoutException();
                return snapshot;
            }
        }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public Task WaitParkedAsync() => _parked.Task.WaitAsync(_parkTimeout);

        public void Release() => _release.TrySetResult();

        public Task MutateAndSaveAsync(Action<AppConfiguration> mutate)
        {
            mutate(config);
            return Task.CompletedTask;
        }

        public Task ExclusiveAsync(Func<Task> action) => action();

        public Task SaveAsync() => Task.CompletedTask;

        private static AppConfiguration Snapshot(AppConfiguration source) => new()
        {
            ChartsByConnection = source.ChartsByConnection.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList())
        };
    }
}
