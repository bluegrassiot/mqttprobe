using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Telemetry;
using MqttProbe.Shared.Tests.TestHelpers;
using ChartsComponent = MqttProbe.Components.Charts.Charts;

namespace MqttProbe.Shared.Tests.Components.Charts;

[TestFixture]
public class ChartsComponentTests : BunitTestContext
{
    private ISettingsStore _mockChartStore = null!;
    private IChartDataService _mockChartDataService = null!;
    private ISessionState _mockSessionState = null!;
    private static readonly Guid _testConnectionId = Guid.NewGuid();

    [SetUp]
    public void SetupMocks()
    {
        _mockSessionState = Substitute.For<ISessionState>();
        _mockSessionState.SelectedConnection.Returns(new Connection { Id = _testConnectionId });

        _mockChartStore = Substitute.For<ISettingsStore>();
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        _mockChartStore.AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        _mockChartStore.RemoveChartAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(Task.CompletedTask);

        _mockChartDataService = Substitute.For<IChartDataService>();
        _mockChartDataService.StartAsync().Returns(Task.CompletedTask);
        _mockChartDataService.GetPoints(Arg.Any<Guid>()).Returns([]);

        Services.AddSingleton(_mockChartStore);
        Services.AddSingleton(_mockChartDataService);
        Services.AddSingleton(_mockSessionState);
        Services.AddSingleton(Substitute.For<IUxTelemetryService>());

        EnsureMudProviders();
    }

    [Test]
    public Task Renders_EmptyState_WhenNoChartsExist()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);

        var cut = Render<ChartsComponent>();

        cut.Markup.Should().Contain("No charts yet");
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
