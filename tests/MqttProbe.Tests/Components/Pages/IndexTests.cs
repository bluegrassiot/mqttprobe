using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Emulation;
using MqttProbe.Components.Layout;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Emulation;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;
using ChartsComponent = MqttProbe.Components.Charts.Charts;
using IndexPage = MqttProbe.Components.Pages.Index;
using NotFoundPage = MqttProbe.Components.Pages.NotFoundPage;

namespace MqttProbe.Shared.Tests.Components.Pages;

[TestFixture]
public class IndexTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private ISettingsStore _mockChartStore = null!;
    private ISubscriptionManager _mockSubManager = null!;
    private IEmulationService _mockEmulation = null!;
    private List<EmulatorNodeConfig> _emulatorNodes = null!;
    private ISparkplugTopologyService _mockTopology = null!;
    private ISettingsStore _mockConfig = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockChartStore = Substitute.For<ISettingsStore>();
        _mockSubManager = Substitute.For<ISubscriptionManager>();
        _mockEmulation = Substitute.For<IEmulationService>();
        _mockTopology = Substitute.For<ISparkplugTopologyService>();
        _mockConfig = Substitute.For<ISettingsStore>();
        _mockConfig.IsHintDismissed(Arg.Any<string>()).Returns(false);

        var mockSessionState = Substitute.For<ISessionState>();
        mockSessionState.SelectedConnection.Returns(new Connection());

        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>());
        _mockChartStore.GetCharts(Arg.Any<Guid>()).Returns([]);
        _mockSubManager.Topics.Returns(new HashSet<string>());
        _emulatorNodes = [];
        _mockEmulation.Nodes.Returns(_ => _emulatorNodes);
        _mockEmulation.IsRunning.Returns(false);
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());

        Services.AddSingleton(_mockMsgStore);
        Services.AddSingleton(_mockChartStore);
        Services.AddSingleton(_mockSubManager);
        Services.AddSingleton(_mockEmulation);
        Services.AddSingleton(_mockTopology);
        Services.AddSingleton(_mockConfig);
        Services.AddSingleton(mockSessionState);
        Services.AddSingleton<IThemes>(new Themes());

        ComponentFactories.AddStub<BrowserPanel>();
        ComponentFactories.AddStub<ChartsComponent>();
        ComponentFactories.AddStub<Subscriptions>();
        ComponentFactories.AddStub<SparkplugNodesView>();
        ComponentFactories.AddStub<Publishers>();
        ComponentFactories.AddStub<EmulationPanel>();
        ComponentFactories.AddStub<MqttProbe.Components.Pages.Settings>();

        AuthorizationContext.SetAuthorized("testuser").SetRoles(AppRoles.Operator);
        EnsureMudProviders();
    }

    [Test]
    public void TopologyChanged_UpdatesNodesTabLabel()
    {
        var group = new SpbGroup { };
        group.Nodes["sensor-01"] = new SpbNode { NodeId = "sensor-01", GroupId = "plant1", Status = SpbNodeStatus.Online };

        var cut = Render<IndexPage>();
        cut.Markup.Should().NotContain(">Nodes<");

        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["plant1"] = group });
        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.Markup.Should().Contain("Nodes (1)");
    }

    [Test]
    public void EmulationTabLabel_WithZeroNodes_ShowsNoCount()
    {
        var cut = Render<IndexPage>();

        cut.Markup.Should().Contain("Emulation");
        cut.Markup.Should().NotContain("Emulation (");
    }

    [Test]
    public void EmulationTabLabel_WithConfiguredNodes_ShowsNodeCount()
    {
        _emulatorNodes.Add(new EmulatorNodeConfig { NodeId = "Node-1" });
        _emulatorNodes.Add(new EmulatorNodeConfig { NodeId = "Node-2" });

        var cut = Render<IndexPage>();

        cut.Markup.Should().Contain("Emulation (2)");
    }

    [Test]
    public void EmulationHint_WithZeroNodes_IsVisible()
    {
        var cut = Render<IndexPage>();

        cut.Markup.Should().Contain("add an emulator node");
    }

    [Test]
    public void EmulationHint_WithConfiguredNodes_IsHidden()
    {
        _emulatorNodes.Add(new EmulatorNodeConfig { NodeId = "Node-1" });

        var cut = Render<IndexPage>();

        cut.Markup.Should().NotContain("add an emulator node");
    }

    [Test]
    public void TabRouting_DefaultTab_IsBrowser()
    {
        var cut = Render<IndexPage>();
        var activeTabs = cut.FindAll(".mud-tab[aria-selected='true']");
        activeTabs.Should().ContainSingle()
            .Which.TextContent.Should().Contain("Browser");
    }

    [Test]
    public void TabRouting_TabSettings_OpensSettingsTab()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=settings");

        var cut = Render<IndexPage>();
        var activeTabs = cut.FindAll(".mud-tab[aria-selected='true']");
        activeTabs.Should().ContainSingle()
            .Which.TextContent.Should().Contain("Settings");
    }

    [Test]
    public void TabRouting_UnknownSlug_DefaultsToBrowser()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=garbage");

        var cut = Render<IndexPage>();
        var activeTabs = cut.FindAll(".mud-tab[aria-selected='true']");
        activeTabs.Should().ContainSingle()
            .Which.TextContent.Should().Contain("Browser");
    }

    [Test]
    public void MobileNav_RendersNavElement()
    {
        var cut = Render<IndexPage>();

        cut.FindAll(".mobile-primary-nav").Should().ContainSingle();
    }

    [Test]
    public void MobileNav_ShowsPrimaryDestinations()
    {
        var cut = Render<IndexPage>();

        var navButtons = cut.FindAll(".mobile-nav-btn");
        navButtons.Should().Contain(b => b.TextContent.Contains("Browser"));
        navButtons.Should().Contain(b => b.TextContent.Contains("Charts"));
        navButtons.Should().Contain(b => b.TextContent.Contains("Publish"));
    }

    [Test]
    public void MobileNav_ShowsNodes_WhenTopologyHasGroups()
    {
        var group = new SpbGroup { };
        group.Nodes["n1"] = new SpbNode { NodeId = "n1", GroupId = "g1", Status = SpbNodeStatus.Online };
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["g1"] = group });

        var cut = Render<IndexPage>();

        cut.FindAll(".mobile-nav-btn")
            .Should().Contain(b => b.TextContent.Contains("Nodes"));
    }

    [Test]
    public void MobileNav_HidesNodes_WhenTopologyEmpty()
    {
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup>());

        var cut = Render<IndexPage>();

        cut.FindAll(".mobile-nav-btn")
            .Should().NotContain(b => b.TextContent.Contains("Nodes"));
    }

    [Test]
    public void MoreMenu_RendersActivator()
    {
        var cut = Render<IndexPage>();

        cut.FindAll(".mobile-nav-more").Should().ContainSingle();
    }

    [Test]
    public void MoreMenu_SubscriptionsIsUrlAddressable()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=subscriptions");

        var cut = Render<IndexPage>();

        cut.FindAll(".mud-tab[aria-selected='true']")
            .Should().ContainSingle()
            .Which.TextContent.Should().Contain("Subscriptions");
    }

    [Test]
    public void MoreMenu_SettingsIsUrlAddressable()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=settings");

        var cut = Render<IndexPage>();

        cut.FindAll(".mud-tab[aria-selected='true']")
            .Should().ContainSingle()
            .Which.TextContent.Should().Contain("Settings");
    }

    [Test]
    public void MobileNav_ClickingCharts_ActivatesChartsTab()
    {
        var cut = Render<IndexPage>();
        var nav = Services.GetRequiredService<NavigationManager>();

        var chartsBtn = cut.FindAll(".mobile-nav-btn")
            .First(b => b.TextContent.Contains("Charts"));
        chartsBtn.Click();

        cut.FindAll(".mud-tab[aria-selected='true']")
            .Should().ContainSingle()
            .Which.TextContent.Should().Contain("Charts");
        nav.Uri.Should().Contain("tab=charts");
    }

    [Test]
    public void MobileNav_HidesPublish_ForNonOperator()
    {
        AuthorizationContext.SetAuthorized("viewer").SetRoles();

        var cut = Render<IndexPage>();

        cut.FindAll(".mobile-nav-btn")
            .Should().NotContain(b => b.TextContent.Contains("Publish"));
    }

    [Test]
    [TestCase("charts", "Charts")]
    [TestCase("subscriptions", "Subscriptions")]
    [TestCase("publish", "Publish")]
    [TestCase("emulation", "Emulation")]
    [TestCase("settings", "Settings")]
    public void TabRouting_SlugWithNoNodes_ActivatesIntendedTab(string slug, string expectedLabel)
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/?tab={slug}");

        var cut = Render<IndexPage>();
        var activeTabs = cut.FindAll(".mud-tab[aria-selected='true']");
        activeTabs.Should().ContainSingle()
            .Which.TextContent.Should().Contain(expectedLabel);
    }

    [Test]
    public void MobileNav_ClickingCharts_NoNodes_ActivatesChartsAndWritesUrl()
    {
        var cut = Render<IndexPage>();
        var nav = Services.GetRequiredService<NavigationManager>();

        var chartsBtn = cut.FindAll(".mobile-nav-btn")
            .First(b => b.TextContent.Contains("Charts"));
        chartsBtn.Click();

        cut.FindAll(".mud-tab[aria-selected='true']")
            .Should().ContainSingle()
            .Which.TextContent.Should().Contain("Charts");
        nav.Uri.Should().Contain("tab=charts");
    }

    [Test]
    public void DesktopTabClick_Charts_NoNodes_WritesCorrectSlug()
    {
        var cut = Render<IndexPage>();
        var nav = Services.GetRequiredService<NavigationManager>();

        var chartsTab = cut.FindAll(".mud-tab")
            .First(t => t.TextContent.Contains("Charts"));
        chartsTab.Click();

        nav.Uri.Should().Contain("tab=charts");
    }

    [Test]
    public void DesktopTabClick_Subscriptions_NoNodes_WritesCorrectSlug()
    {
        var cut = Render<IndexPage>();
        var nav = Services.GetRequiredService<NavigationManager>();

        var subsTab = cut.FindAll(".mud-tab")
            .First(t => t.TextContent.Contains("Subscriptions"));
        subsTab.Click();

        nav.Uri.Should().Contain("tab=subscriptions");
    }

    [Test]
    public void TabRouting_NodesSlug_EmptyTopology_DefaultsToBrowser()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=nodes");

        var cut = Render<IndexPage>();
        var activeTabs = cut.FindAll(".mud-tab[aria-selected='true']");
        activeTabs.Should().ContainSingle()
            .Which.TextContent.Should().Contain("Browser");
    }

    [Test]
    public void TopologyChanged_PreservesActiveSlugWhenStillVisible()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/?tab=charts");

        var cut = Render<IndexPage>();

        cut.FindAll(".mud-tab[aria-selected='true']")
            .Should().ContainSingle()
            .Which.TextContent.Should().Contain("Charts");

        var group = new SpbGroup { };
        group.Nodes["n1"] = new SpbNode { NodeId = "n1", GroupId = "g1", Status = SpbNodeStatus.Online };
        _mockTopology.Groups.Returns(new Dictionary<string, SpbGroup> { ["g1"] = group });
        _mockTopology.TopologyChanged += Raise.Event<Action>();

        cut.FindAll(".mud-tab[aria-selected='true']")
            .Should().ContainSingle()
            .Which.TextContent.Should().Contain("Charts");
    }

}

[TestFixture]
public class NotFoundPageTests : BunitTestContext
{
    [SetUp]
    public void SetupMocks()
    {
        var mqttClient = Substitute.For<IManagedMqttClient>();
        mqttClient.IsConnected.Returns(true);
        mqttClient.IsStarted.Returns(true);
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetVersion().Returns("1.0.0-test");
        appInfo.RequiresAuthentication.Returns(false);
        var sessionState = Substitute.For<ISessionState>();
        sessionState.SelectedConnection.Returns(new Connection());

        Services.AddSingleton(mqttClient);
        Services.AddSingleton(appInfo);
        Services.AddSingleton(sessionState);
        Services.AddSingleton(Substitute.For<IMessageStoreManager>());
        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<IConnectionSessionLifecycle>());

        var mockConfig = Substitute.For<ISettingsStore>();
        mockConfig.Config.Returns(new AppConfiguration());
        Services.AddSingleton(mockConfig);
        Services.AddSingleton(Substitute.For<IJSRuntime>());
        var mockMetrics = Substitute.For<IUxMetricsService>();
        mockMetrics.GetSnapshot().Returns(new UxMetricsSnapshot(
            ConnectAttempts: 0, ConnectSuccesses: 0, ConnectFailures: 0,
            PublishSuccesses: 0, PublishFailures: 0,
            ChartsCreated: 0, SeriesAddedToExistingCharts: 0,
            MessagesProcessed: 0, MessagesDropped: 0,
            AvgProcessingTimeUs: 0, MaxProcessingTimeUs: 0,
            AvgPayloadBytes: 0, MaxPayloadBytes: 0,
            CurrentMessagesPerSecond: 0,
            MessageRateHistory: new int[UxMetricsService.RateWindowSeconds],
            MessagesProcessedByFormat: new Dictionary<string, long>(),
            ChartFunnelBySource: new Dictionary<string, long>(),
            MaxDisplayMessages: 0, CurrentDisplayedMessageCount: 0,
            AppHealth: new AppHealthMetricsSnapshot(
                CpuUsagePercent: 0, ManagedHeapMb: 0,
                WorkingSetMb: 0, ThreadCount: 0, ThreadPoolQueueLength: 0,
                GcGen2Collections: 0, UptimeSeconds: 0),
            EmulatorPublishersOnline: 0,
            EmulatorPublishCycles: 0, EmulatorNodesInError: 0));
        Services.AddSingleton(mockMetrics);
        Services.AddSingleton<IThemes>(new Themes());
        var mockUpdateService = Substitute.For<IUpdateService>();
        mockUpdateService.IsSupported.Returns(false);
        Services.AddSingleton(mockUpdateService);
    }

    [Test]
    public void Renders_NotFoundMessage()
    {
        var cut = Render<NotFoundPage>();

        cut.Markup.Should().Contain("Sorry, there's nothing at this address.");
        cut.Find("[role='alert']").Should().NotBeNull();
    }

    [Test]
    public void Renders_HomeLink_TargetingRoot()
    {
        var cut = Render<NotFoundPage>();

        cut.FindAll("a").Should().Contain(a => a.GetAttribute("href") == "/");
    }
}
