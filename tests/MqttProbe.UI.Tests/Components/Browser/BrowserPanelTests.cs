using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class BrowserPanelTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private IUiSettings _mockConfig = null!;
    private IDialogService _mockDialogService = null!;
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.RootTopicCount.Returns(0);

        _mockConfig = Substitute.For<IUiSettings>();
        _mockConfig.IsHintDismissed(Arg.Any<string>()).Returns(false);

        _mockDialogService = Substitute.For<IDialogService>();
        _mockSnackbar = Substitute.For<ISnackbar>();

        Services.AddSingleton(_mockMsgStore);
        Services.AddUiSettings(_mockConfig);
        Services.AddSingleton(_mockDialogService);
        Services.AddSingleton(_mockSnackbar);
        Services.AddSingleton(Substitute.For<ITopicExcludeService>());

        ComponentFactories.AddStub<TopicBrowser>();
        ComponentFactories.AddStub<PayloadBrowser>();
        ComponentFactories.AddStub<MobileTopicBar>();

        EnsureMudProviders();
    }

    [Test]
    public void Renders_Header_With_Title_And_Count_Chip()
    {
        _mockMsgStore.RootTopicCount.Returns(1);

        var cut = Render<BrowserPanel>();

        cut.Markup.Should().Contain("app-tabpanel-header__row");
        cut.Markup.Should().Contain(">Browser<");
        cut.Markup.Should().Contain("1");
    }

    [Test]
    public void Header_OffersNoClearAllMessagesControl()
    {
        _mockMsgStore.RootTopicCount.Returns(1);

        var cut = Render<BrowserPanel>();

        cut.FindAll("button")
            .Should().NotContain(button => button.TextContent.Contains("Clear all messages"),
                "clearing works on the selected branches, so the global clear is gone");
    }

    [Test]
    public void BrowserHint_WhenNoStoresAndNotDismissed_IsVisible()
    {
        var cut = Render<BrowserPanel>();

        cut.Markup.Should().Contain("connect to a broker");
    }

    [Test]
    public void BrowserHint_WhenStoresExist_IsHidden()
    {
        _mockMsgStore.RootTopicCount.Returns(1);

        var cut = Render<BrowserPanel>();

        cut.Markup.Should().NotContain("connect to a broker");
    }

    [Test]
    public void MobileLayout_RendersMobileTopicBar()
    {
        var cut = Render<BrowserPanel>();

        // MobileTopicBar is stubbed (renders empty), but the mobile layout wrapper exists
        cut.Markup.Should().Contain("browser-mobile-layout");
    }

    [Test]
    public void MobileLayout_RendersTopicPickerOverlay()
    {
        var cut = Render<BrowserPanel>();

        // TopicPickerOverlay is in the render tree (IsOpen=false so it renders nothing,
        // but the component is wired up)
        cut.FindComponents<TopicPickerOverlay>().Should().HaveCount(1);
    }

    [Test]
    public void MobilePicker_OpensAndCloses()
    {
        _mockMsgStore.RootTopicCount.Returns(1);

        var cut = Render<BrowserPanel>();

        // Initially closed — overlay panel should not be rendered
        cut.Markup.Should().NotContain("topic-picker-overlay__panel");

        // Open picker (internal method, matching OnTimerTick test pattern)
        cut.Instance.OpenMobilePicker();
        cut.Render();

        cut.Markup.Should().Contain("topic-picker-overlay__panel");

        // Close picker
        cut.Instance.CloseMobilePicker();
        cut.Render();

        cut.Markup.Should().NotContain("topic-picker-overlay__panel");
    }
}
