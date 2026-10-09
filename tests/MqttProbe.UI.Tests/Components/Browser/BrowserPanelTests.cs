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
    public void ClearMessagesButton_WhenNoStores_IsDisabled()
    {
        var cut = Render<BrowserPanel>();

        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear"));
        btn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void ClearMessagesButton_WhenStoresExist_IsEnabled()
    {
        _mockMsgStore.RootTopicCount.Returns(1);

        var cut = Render<BrowserPanel>();

        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear"));
        btn.HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task ClearMessages_WhenConfirmed_CallsStoreClear()
    {
        _mockMsgStore.RootTopicCount.Returns(1);
        _mockMsgStore.ClearAllMessages().Returns(Task.CompletedTask);
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear"));

        await cut.InvokeAsync(() => btn.Click());

        _ = _mockMsgStore.Received(1).ClearAllMessages();
    }

    [Test]
    public async Task ClearMessages_WhenCancelled_DoesNotCallStoreClear()
    {
        _mockMsgStore.RootTopicCount.Returns(1);
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear"));

        await cut.InvokeAsync(() => btn.Click());

        _ = _mockMsgStore.DidNotReceive().ClearAllMessages();
    }

    [Test]
    public async Task ClearMessages_WhenConfirmed_ShowsSuccessSnackbar()
    {
        _mockMsgStore.RootTopicCount.Returns(1);
        _mockMsgStore.ClearAllMessages().Returns(Task.CompletedTask);
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear"));

        await cut.InvokeAsync(() => btn.Click());

        _mockSnackbar.Received(1).Add("All stored messages cleared.", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task ClearMessages_AfterClear_DisablesButtonOnNextTick()
    {
        var rootCount = 1;
        _mockMsgStore.RootTopicCount.Returns(_ => rootCount);

        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));

        _mockMsgStore.ClearAllMessages().Returns(Task.CompletedTask)
            .AndDoes(_ => rootCount = 0);

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear"));

        btn.HasAttribute("disabled").Should().BeFalse();

        await cut.InvokeAsync(() => btn.Click());

        cut.Instance.OnTimerTick();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button")
                .First(b => b.TextContent.Contains("Clear"))
                .HasAttribute("disabled").Should().BeTrue();
        });
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
