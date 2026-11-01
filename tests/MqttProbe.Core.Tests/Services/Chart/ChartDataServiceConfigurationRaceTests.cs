using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Tests.Utilities;

namespace MqttProbe.Core.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceConfigurationRaceTests
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
    public async Task SourceChangeWhileMessageInFlight_DropsStalePointAndKeepsUnrelatedSeries()
    {
        var connectionId = Guid.NewGuid();
        var changed = new ChartSeries { Topic = "race/changed", JsonPath = "value" };
        var unrelated = new ChartSeries { Topic = "race/unrelated", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 100, Series = [changed, unrelated] };
        await SeedAsync(connectionId, chart);
        await FireAsync("race/unrelated", """{"value": 7}""");

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("race/changed", """{"value": 1}"""));
        await _document.WaitParkedAsync();
        try
        {
            await _charts.UpdateChartAsync(connectionId, ReplaceSeries(chart,
                new ChartSeries { Id = changed.Id, Topic = changed.Topic, JsonPath = "other" },
                unrelated));
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(changed.Id).Should().BeEmpty();
        _service.GetPoints(unrelated.Id).Should().ContainSingle().Which.Value.Should().Be(7);

        await FireAsync("race/changed", """{"value": 1, "other": 2}""");
        _service.GetPoints(changed.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Test]
    public async Task ChartRemovalWhileMessageInFlight_DropsStalePointAndKeepsOtherChart()
    {
        var connectionId = Guid.NewGuid();
        var removed = new ChartSeries { Topic = "race/removed", JsonPath = "value" };
        var kept = new ChartSeries { Topic = "race/kept", JsonPath = "value" };
        var removedChart = new ChartConfiguration { MaxPoints = 100, Series = [removed] };
        var keptChart = new ChartConfiguration { MaxPoints = 100, Series = [kept] };
        await SeedAsync(connectionId, removedChart, keptChart);
        await FireAsync("race/kept", """{"value": 7}""");
        await FireAsync("race/removed", """{"value": 1}""");

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("race/removed", """{"value": 2}"""));
        await _document.WaitParkedAsync();
        try
        {
            await _charts.RemoveChartAsync(connectionId, removedChart.Id);
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(removed.Id).Should().BeEmpty();
        _service.GetPoints(kept.Id).Should().ContainSingle().Which.Value.Should().Be(7);
    }

    [Test]
    public async Task SourceToggledAwayAndBackWhileMessageInFlight_DropsStalePoint()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Topic = "race/aba", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 100, Series = [series] };
        await SeedAsync(connectionId, chart);

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("race/aba", """{"value": 1}"""));
        await _document.WaitParkedAsync();
        try
        {
            await _charts.UpdateChartAsync(connectionId, ReplaceSeries(chart,
                new ChartSeries { Id = series.Id, Topic = series.Topic, JsonPath = "other" }));
            await _charts.UpdateChartAsync(connectionId, ReplaceSeries(chart,
                new ChartSeries { Id = series.Id, Topic = series.Topic, JsonPath = "value" }));
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(series.Id).Should().BeEmpty();

        await FireAsync("race/aba", """{"value": 2}""");
        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Test]
    public async Task UnrelatedSeriesChangeWhileMessageInFlight_StillAcceptsPoint()
    {
        var connectionId = Guid.NewGuid();
        var target = new ChartSeries { Topic = "race/target", JsonPath = "value" };
        var other = new ChartSeries { Topic = "race/other", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 100, Series = [target, other] };
        await SeedAsync(connectionId, chart);

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("race/target", """{"value": 1}"""));
        await _document.WaitParkedAsync();
        try
        {
            await _charts.UpdateChartAsync(connectionId, ReplaceSeries(chart,
                target,
                new ChartSeries { Id = other.Id, Topic = other.Topic, JsonPath = "moved" }));
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(target.Id).Should().ContainSingle().Which.Value.Should().Be(1);
        _service.GetPoints(other.Id).Should().BeEmpty();
    }

    [Test]
    public async Task ClearBuffersWhileMessageInFlight_DropsAdmittedPoint()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Topic = "race/clear", JsonPath = "value" };
        var chart = new ChartConfiguration { MaxPoints = 100, Series = [series] };
        await SeedAsync(connectionId, chart);

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("race/clear", """{"value": 1}"""));
        await _document.WaitParkedAsync();
        try
        {
            _service.ClearBuffers();
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(series.Id).Should().BeEmpty();

        await FireAsync("race/clear", """{"value": 2}""");
        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Test]
    public async Task ConnectionSwitchWhileMessageInFlight_DropsAdmittedPoint()
    {
        var connectionA = Guid.NewGuid();
        var connectionB = Guid.NewGuid();
        var seriesA = new ChartSeries { Topic = "race/a", JsonPath = "value" };
        var seriesB = new ChartSeries { Topic = "race/b", JsonPath = "value" };
        await SeedAsync(connectionA, new ChartConfiguration { MaxPoints = 100, Series = [seriesA] });
        await _charts.AddChartAsync(connectionB,
            new ChartConfiguration { MaxPoints = 100, Series = [seriesB] });

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("race/a", """{"value": 1}"""));
        await _document.WaitParkedAsync();
        try
        {
            _service.SetConnection(connectionB);
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(seriesA.Id).Should().BeEmpty();
        _service.GetPoints(seriesB.Id).Should().BeEmpty();
    }

    [Test]
    public async Task SameConnectionRebindWhileMessageInFlight_StillAcceptsPoint()
    {
        var connectionId = Guid.NewGuid();
        var series = new ChartSeries { Topic = "race/rebind", JsonPath = "value" };
        await SeedAsync(connectionId, new ChartConfiguration { MaxPoints = 100, Series = [series] });

        _document.Arm();
        var ingress = Task.Run(() => FireAsync("race/rebind", """{"value": 1}"""));
        await _document.WaitParkedAsync();
        try
        {
            _service.SetConnection(connectionId);
        }
        finally
        {
            _document.Release();
        }

        await ingress.WaitAsync(TimeSpan.FromSeconds(10));

        _service.GetPoints(series.Id).Should().ContainSingle().Which.Value.Should().Be(1);
    }

    private async Task SeedAsync(Guid connectionId, params ChartConfiguration[] charts)
    {
        foreach (var chart in charts)
            await _charts.AddChartAsync(connectionId, chart);
        _service.SetConnection(connectionId);
        await _service.StartAsync();
    }

    private Task FireAsync(string topic, string payload) => _handler!(MakeArgs(topic, payload));

    private static ChartConfiguration ReplaceSeries(ChartConfiguration chart, params ChartSeries[] series) =>
        new() { Id = chart.Id, Name = chart.Name, MaxPoints = chart.MaxPoints, Series = [.. series] };

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
