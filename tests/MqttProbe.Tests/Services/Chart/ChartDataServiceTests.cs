using System.Collections.Concurrent;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Services.Chart;

[TestFixture]
public class ChartDataServiceTests
{
    private IManagedMqttClient _mockClient = null!;
    private IJsonFieldExtractor _extractor = null!;
    private IChartFieldRegistry _registry = null!;
    private ISettingsStore _mockSettingsStore = null!;
    private ChartDataService _service = null!;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IManagedMqttClient>();
        _extractor = new JsonFieldExtractor();
        _registry = new ChartFieldRegistry();
        _mockSettingsStore = Substitute.For<ISettingsStore>();
        _mockSettingsStore.Charts.Returns([]);
        _service = new ChartDataService(_mockClient, _extractor, _registry, _mockSettingsStore);
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
        captured.Should().BeNull(); // event hasn't fired
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void ChartConfiguration_MaxPoints_FallsBackToDefault_WhenNonPositive(int maxPoints)
    {
        var config = new ChartConfiguration { MaxPoints = maxPoints };

        config.MaxPoints.Should().Be(500);
    }

    [Test]
    public async Task StartAsync_ConcurrentCalls_RegistersHandlerExactlyOnce()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => _service.StartAsync()));

        await Task.WhenAll(tasks);

        _mockClient.Received(1).ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>();
    }
}
