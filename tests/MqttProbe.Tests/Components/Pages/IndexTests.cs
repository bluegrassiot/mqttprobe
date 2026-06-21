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

        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>());
        _mockChartStore.Charts.Returns([]);
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
        Services.AddSingleton<IThemes>(new Themes());

        ComponentFactories.AddStub<TopicBrowser>();
        ComponentFactories.AddStub<PayloadBrowser>();
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
    public void Browser_Tab_Renders_Header_With_Title_And_Count_Chip()
    {
        // Seed one message store so the count chip renders.
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["a/b"] = new MessageStore { Topic = "a/b", FullTopic = "a/b" } }));

        var cut = Render<IndexPage>();

        // The panel-level header now wraps the Browser tab.
        cut.Markup.Should().Contain("app-tabpanel-header__row");
        // The title text is the Browser tab name.
        cut.Markup.Should().Contain(">Browser<");
        // The count chip shows the store count (1).
        cut.Markup.Should().Contain("1");
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

        var mockConfig = Substitute.For<ISettingsStore>();
        mockConfig.Config.Returns(new AppConfiguration());
        Services.AddSingleton(mockConfig);
        Services.AddSingleton(Substitute.For<IJSRuntime>());
        Services.AddSingleton<IThemes>(new Themes());
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
