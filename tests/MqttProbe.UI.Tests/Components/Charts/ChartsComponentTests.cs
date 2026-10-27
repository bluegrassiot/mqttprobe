using ApexCharts;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.UI.Tests.TestHelpers;
using Org.Eclipse.Tahu.Protobuf;
using ChartsComponent = MqttProbe.UI.Components.Charts.Charts;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartsComponentTests : BunitTestContext
{
    private IChartSettings _mockChartStore = null!;
    private IChartDataService _mockChartDataService = null!;
    private ISessionState _mockSessionState = null!;
    private static readonly Guid _testConnectionId = Guid.NewGuid();

    [SetUp]
    public void SetupMocks()
    {
        _mockSessionState = Substitute.For<ISessionState>();
        _mockSessionState.SelectedConnection.Returns(new Connection { Id = _testConnectionId });

        _mockChartStore = Substitute.For<IChartSettings>();
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        _mockChartStore.AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        _mockChartStore.RemoveChartAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(Task.CompletedTask);

        _mockChartDataService = Substitute.For<IChartDataService>();
        _mockChartDataService.StartAsync().Returns(Task.CompletedTask);
        _mockChartDataService.GetPoints(Arg.Any<Guid>()).Returns([]);

        Services.AddChartSettings(_mockChartStore);
        Services.AddSingleton(_mockChartDataService);
        Services.AddSingleton(_mockSessionState);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());

        EnsureMudProviders();
    }

    [Test]
    public Task Renders_EmptyState_WhenNoChartsExist()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);

        var cut = Render<ChartsComponent>();

        cut.Markup.Should().Contain("No charts yet");
        cut.Markup.Should().Contain("select a node or device, then choose the chart icon");
        cut.Markup.Should().Contain("values are not exposed under a stable metric name");
        cut.Markup.Should().NotContain("open a metric");
        return Task.CompletedTask;
    }

    [Test]
    public Task Renders_ChartCardForEachConfiguration()
    {
        var configs = new List<ChartConfiguration>
        {
            new() { Name = "Temp Chart" },
            new() { Name = "Pressure Chart" }
        };
        _mockChartStore.GetCharts(_testConnectionId).Returns(configs);

        var cut = Render<ChartsComponent>();

        cut.Markup.Should().Contain("Temp Chart");
        cut.Markup.Should().Contain("Pressure Chart");
        return Task.CompletedTask;
    }

    [Test]
    public void Charts_AfterConfigurationRemoved_RendersOnlyRemainingCard()
    {
        var a = new ChartConfiguration { Name = "Chart A" };
        var b = new ChartConfiguration { Name = "Chart B" };
        var configs = new List<ChartConfiguration> { a, b };
        _mockChartStore.GetCharts(_testConnectionId).Returns(configs);

        var cut = Render<ChartsComponent>();
        cut.Markup.Should().Contain("Chart A");
        cut.Markup.Should().Contain("Chart B");

        configs.Remove(a);
        _mockChartStore.ChartsChanged += Raise.Event<Action<Guid>>(_testConnectionId);

        cut.Markup.Should().NotContain("Chart A");
        cut.Markup.Should().Contain("Chart B");
    }

    [Test]
    public void ConfigurationsChanged_SubscribedExactlyOnce()
    {
        var subscribeCount = 0;
        _mockChartStore.When(x => x.ChartsChanged += Arg.Any<Action<Guid>>())
            .Do(_ => subscribeCount++);

        Render<ChartsComponent>();

        subscribeCount.Should().Be(1);
    }

    [Test]
    public void ConfigurationEdit_ReflectedViaConfigurationsChanged()
    {
        var config = new ChartConfiguration { Name = "Original" };
        var configs = new List<ChartConfiguration> { config };
        _mockChartStore.GetCharts(_testConnectionId).Returns(configs);

        var cut = Render<ChartsComponent>();
        cut.Markup.Should().Contain("Original");

        config.Name = "Renamed";
        _mockChartStore.ChartsChanged += Raise.Event<Action<Guid>>(_testConnectionId);

        cut.Markup.Should().Contain("Renamed");
    }

    [Test]
    public async Task DisposeAsync_UnsubscribesFromConfigurationsChanged()
    {
        bool unsubscribed = false;
        _mockChartStore
            .When(x => x.ChartsChanged -= Arg.Any<Action<Guid>?>())
            .Do(_ => unsubscribed = true);

        var cut = Render<ChartsComponent>();
        await cut.Instance.DisposeAsync();

        unsubscribed.Should().BeTrue("DisposeAsync must unregister the ChartsChanged handler");
    }

    [Test]
    public async Task OnInitializedAsync_CallsChartDataServiceStartAsync()
    {
        Render<ChartsComponent>();

        await _mockChartDataService.Received(1).StartAsync();
    }

    [Test]
    public void OnInitializedAsync_CallsSetConnectionWithSelectedConnectionId()
    {
        Render<ChartsComponent>();

        _mockChartDataService.Received(1).SetConnection(_testConnectionId);
    }

    [Test]
    public void SelectedConnectionChanged_CallsSetConnectionWithNewConnectionId()
    {
        var newConnectionId = Guid.NewGuid();
        var newConnection = new Connection { Id = newConnectionId };

        var cut = Render<ChartsComponent>();
        _mockChartDataService.ClearReceivedCalls();

        _mockSessionState.SelectedConnection.Returns(newConnection);
        _mockSessionState.SelectedConnectionChanged += Raise.Event<Action<Connection>>(newConnection);

        _mockChartDataService.Received(1).SetConnection(newConnectionId);
    }

    [Test]
    public void Renders_Header_With_Title_And_Create_Action()
    {
        var cut = Render<ChartsComponent>();

        cut.Markup.Should().Contain("app-tabpanel-header__row");
        cut.Markup.Should().Contain("mud-typography-h6");
        cut.Markup.Should().Contain(">Charts<");
        cut.Markup.Should().Contain("Create");
        cut.Markup.Should().NotContain("Create chart", "label was shortened to verb-only");
    }
}

[TestFixture]
public class ChartsRealCollectorComponentTests : BunitTestContext
{
    private static readonly Guid _testConnectionId = Guid.NewGuid();
    private IChartSettings _chartSettings = null!;
    private ISessionState _sessionState = null!;
    private IMqttManagedClient _client = null!;
    private ChartDataService _chartDataService = null!;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _handler;

    [SetUp]
    public void Setup()
    {
        const string nodeTopic = "spBv1.0/g/NDATA/node-1";
        const string deviceTopic = "spBv1.0/g/DDATA/node-1/device-1";
        _sessionState = Substitute.For<ISessionState>();
        _sessionState.SelectedConnection.Returns(new Connection { Id = _testConnectionId });
        _chartSettings = Substitute.For<IChartSettings>();
        _chartSettings.GetCharts(_testConnectionId).Returns(
        [
            new ChartConfiguration
            {
                Name = "Sparkplug chart",
                Series =
                [
                    new ChartSeries { Id = Guid.NewGuid(), Topic = nodeTopic, JsonPath = "metrics.Set Point" },
                    new ChartSeries { Id = Guid.NewGuid(), Topic = deviceTopic, JsonPath = "metrics.Device Temp" }
                ]
            }
        ]);
        _client = Substitute.For<IMqttManagedClient>();
        _client.When(x => x.ApplicationMessageReceivedAsync += Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(x => _handler = x.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());
        var registryBuilder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(registryBuilder);
        var pipeline = new PayloadPipeline(registryBuilder.Build(), Substitute.For<ILogger<PayloadPipeline>>());
        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings());
        _chartDataService = new ChartDataService(_client, new JsonFieldExtractor(), new ChartFieldRegistry(),
            _chartSettings, pipeline, sparkplugSettings);

        Services.AddChartSettings(_chartSettings);
        Services.AddSingleton<IChartDataService>(_chartDataService);
        Services.AddSingleton(_sessionState);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());
        EnsureMudProviders();
    }

    [TearDown]
    public void TearDown()
    {
        _chartDataService.Dispose();
        _client.Dispose();
    }

    [Test]
    public async Task Charts_RendersNodeAndDevicePointsCollectedBeforeInitialization()
    {
        const string nodeTopic = "spBv1.0/g/NDATA/node-1";
        const string deviceTopic = "spBv1.0/g/DDATA/node-1/device-1";
        var config = _chartSettings.GetCharts(_testConnectionId).Single();
        var nodeSeries = config.Series[0];
        var deviceSeries = config.Series[1];
        _chartDataService.SetConnection(_testConnectionId);
        await _chartDataService.StartAsync();

        var nodePayload = new Payload();
        nodePayload.Metrics.Add(new Payload.Types.Metric { Name = "Set Point", Datatype = 10, DoubleValue = 22.5 });
        await _handler!(MakeSparkplugArgs(nodeTopic, nodePayload.ToByteArray()));
        var devicePayload = new Payload();
        devicePayload.Metrics.Add(new Payload.Types.Metric { Name = "Device Temp", Datatype = 10, DoubleValue = 18.25 });
        await _handler!(MakeSparkplugArgs(deviceTopic, devicePayload.ToByteArray()));

        var cut = Render<ChartsComponent>();

        var series = cut.FindComponents<ApexPointSeries<ChartDataPoint>>();
        series.Should().HaveCount(2);
        series[0].Instance.Items.Should().ContainSingle()
            .Which.Value.Should().Be(22.5);
        series[1].Instance.Items.Should().ContainSingle()
            .Which.Value.Should().Be(18.25);
        _chartDataService.GetPoints(nodeSeries.Id).Should().ContainSingle();
        _chartDataService.GetPoints(deviceSeries.Id).Should().ContainSingle();

        var connectionB = Guid.NewGuid();
        var seriesBId = Guid.NewGuid();
        _chartSettings.GetCharts(connectionB).Returns(
        [
            new ChartConfiguration
            {
                Name = "Connection B chart",
                Series = [new ChartSeries { Id = seriesBId, Topic = nodeTopic, JsonPath = "metrics.Set Point" }]
            }
        ]);
        var newConnection = new Connection { Id = connectionB };
        _sessionState.SelectedConnection.Returns(newConnection);
        _sessionState.SelectedConnectionChanged += Raise.Event<Action<Connection>>(newConnection);

        cut.WaitForAssertion(() =>
        {
            _chartDataService.GetPoints(nodeSeries.Id).Should().BeEmpty();
            _chartDataService.GetPoints(deviceSeries.Id).Should().BeEmpty();
            cut.Markup.Should().Contain("Connection B chart");
        });
        cut.FindComponents<ApexPointSeries<ChartDataPoint>>().Should().ContainSingle()
            .Which.Instance.Items.Should().BeEmpty();
    }

    private static MqttApplicationMessageReceivedEventArgs MakeSparkplugArgs(string topic, byte[] payload)
    {
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();
        return new MqttApplicationMessageReceivedEventArgs("test-client", message,
            new MQTTnet.Packets.MqttPublishPacket { Topic = topic }, null);
    }
}
