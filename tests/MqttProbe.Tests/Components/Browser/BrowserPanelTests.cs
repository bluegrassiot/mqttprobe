using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Browser;

[TestFixture]
public class BrowserPanelTests : BunitTestContext
{
    private IMessageStoreManager _mockMsgStore = null!;
    private ISettingsStore _mockConfig = null!;
    private IDialogService _mockDialogService = null!;
    private ISnackbar _mockSnackbar = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>());
        _mockMsgStore.GetMessagesForSelectedTopic()
            .Returns(Task.FromResult<IEnumerable<MqttMessage>>(Array.Empty<MqttMessage>()));

        _mockConfig = Substitute.For<ISettingsStore>();
        _mockConfig.IsHintDismissed(Arg.Any<string>()).Returns(false);

        _mockDialogService = Substitute.For<IDialogService>();
        _mockSnackbar = Substitute.For<ISnackbar>();

        Services.AddSingleton(_mockMsgStore);
        Services.AddSingleton(_mockConfig);
        Services.AddSingleton(_mockDialogService);
        Services.AddSingleton(_mockSnackbar);

        ComponentFactories.AddStub<TopicBrowser>();
        ComponentFactories.AddStub<PayloadBrowser>();

        EnsureMudProviders();
    }

    [Test]
    public void Renders_Header_With_Title_And_Count_Chip()
    {
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["a/b"] = new MessageStore { Topic = "a/b", FullTopic = "a/b" } }));

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
            .First(b => b.TextContent.Contains("Clear Messages"));
        btn.HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public void ClearMessagesButton_WhenStoresExist_IsEnabled()
    {
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["t"] = new MessageStore { Topic = "t", FullTopic = "t" } }));

        var cut = Render<BrowserPanel>();

        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear Messages"));
        btn.HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task ClearMessages_WhenConfirmed_CallsStoreClear()
    {
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["t"] = new MessageStore { Topic = "t", FullTopic = "t" } }));
        _mockMsgStore.ClearAllMessages().Returns(Task.CompletedTask);
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear Messages"));

        await cut.InvokeAsync(() => btn.Click());

        _ = _mockMsgStore.Received(1).ClearAllMessages();
    }

    [Test]
    public async Task ClearMessages_WhenCancelled_DoesNotCallStoreClear()
    {
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["t"] = new MessageStore { Topic = "t", FullTopic = "t" } }));
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(false));

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear Messages"));

        await cut.InvokeAsync(() => btn.Click());

        _ = _mockMsgStore.DidNotReceive().ClearAllMessages();
    }

    [Test]
    public async Task ClearMessages_WhenConfirmed_ShowsSuccessSnackbar()
    {
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["t"] = new MessageStore { Topic = "t", FullTopic = "t" } }));
        _mockMsgStore.ClearAllMessages().Returns(Task.CompletedTask);
        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear Messages"));

        await cut.InvokeAsync(() => btn.Click());

        _mockSnackbar.Received(1).Add("All stored messages cleared.", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task ClearMessages_AfterClear_DisablesButtonOnNextTick()
    {
        var stores = new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["t"] = new MessageStore { Topic = "t", FullTopic = "t" } });
        _mockMsgStore.MessageStores.Returns(stores);

        _mockDialogService.ShowMessageBoxAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Task.FromResult<bool?>(true));

        _mockMsgStore.ClearAllMessages().Returns(Task.CompletedTask)
            .AndDoes(_ => stores.Clear());

        var cut = Render<BrowserPanel>();
        var btn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Clear Messages"));

        btn.HasAttribute("disabled").Should().BeFalse();

        await cut.InvokeAsync(() => btn.Click());

        cut.Instance.OnTimerTick();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button")
                .First(b => b.TextContent.Contains("Clear Messages"))
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
        _mockMsgStore.MessageStores.Returns(new ConcurrentDictionary<string, MessageStore>(
            new Dictionary<string, MessageStore> { ["t"] = new MessageStore { Topic = "t", FullTopic = "t" } }));

        var cut = Render<BrowserPanel>();

        cut.Markup.Should().NotContain("connect to a broker");
    }
}
