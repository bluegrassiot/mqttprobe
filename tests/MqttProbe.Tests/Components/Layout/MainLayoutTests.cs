using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Layout;

[TestFixture]
public class MainLayoutTests : BunitTestContext
{
    private IManagedMqttClient _mockMqttClient = null!;
    private IMessageStoreManager _mockMsgStore = null!;
    private IDialogService _mockDialogService = null!;
    private IAppInfoService _mockAppInfo = null!;
    private ISessionState _mockSessionState = null!;
    private ISettingsStore _mockConfig = null!;
    private IJSRuntime _mockJs = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMqttClient = Substitute.For<IManagedMqttClient>();
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockDialogService = Substitute.For<IDialogService>();
        _mockAppInfo = Substitute.For<IAppInfoService>();
        _mockSessionState = Substitute.For<ISessionState>();
        _mockConfig = Substitute.For<ISettingsStore>();
        _mockConfig.Config.Returns(new AppConfiguration());
        _mockJs = Substitute.For<IJSRuntime>();

        _mockAppInfo.GetVersion().Returns("1.0.0-test");
        _mockAppInfo.RequiresAuthentication.Returns(false);
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(false);
        _mockSessionState.SelectedConnection.Returns(new Connection());

        _mockDialogService
            .ShowAsync<ConnectionDialog>(Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(Substitute.For<IDialogReference>());

        Services.AddSingleton(_mockMqttClient);
        Services.AddSingleton(_mockMsgStore);
        Services.AddSingleton(_mockDialogService);
        Services.AddSingleton(_mockAppInfo);
        Services.AddSingleton(_mockSessionState);
        Services.AddSingleton(_mockConfig);
        Services.AddSingleton(_mockJs);
        Services.AddSingleton<IThemes>(new Themes());
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        return Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(_ => { })));
    }

    private IRenderedComponent<AppShellBar> RenderBar() =>
        Render<AppShellBar>();

    [Test]
    public async Task OnAfterRenderAsync_FirstRender_WhenNotStartedOrConnected_OpensConnectionDialog()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(false);

        RenderLayout();
        await Task.Delay(50); // let OnAfterRenderAsync complete

        await _mockDialogService.Received(1)
            .ShowAsync<ConnectionDialog>(Arg.Any<string>(), Arg.Any<DialogOptions>());
    }

    [Test]
    public async Task OnAfterRenderAsync_WhenAlreadyStarted_DoesNotOpenDialog()
    {
        _mockMqttClient.IsStarted.Returns(true);
        _mockMqttClient.IsConnected.Returns(false);

        RenderLayout();
        await Task.Delay(50);

        await _mockDialogService.DidNotReceive()
            .ShowAsync<ConnectionDialog>(Arg.Any<string>(), Arg.Any<DialogOptions>());
    }

    [Test]
    public async Task ConnectionToggle_WhenConnected_CallsStopAsync()
    {
        _mockMqttClient.IsConnected.Returns(true);
        _mockMqttClient.StopAsync().Returns(Task.CompletedTask);

        var cut = RenderLayout();
        var bar = cut.FindComponent<AppShellBar>();

        await bar.InvokeAsync(() => bar.Instance.ConnectionToggle());

        await _mockMqttClient.Received(1).StopAsync();
    }

    [Test]
    public async Task ConnectionToggle_WhenDisconnected_OpensConnectionDialog()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true); // prevent auto-open on first render

        var cut = RenderLayout();
        await Task.Delay(50);
        _mockDialogService.ClearReceivedCalls();

        var bar = cut.FindComponent<AppShellBar>();
        await bar.InvokeAsync(() => bar.Instance.ConnectionToggle());

        await _mockDialogService.Received(1)
            .ShowAsync<ConnectionDialog>(Arg.Any<string>(), Arg.Any<DialogOptions>());
    }

    [Test]
    public void Renders_AppBarWithVersion_FromAppInfoService()
    {
        var cut = RenderLayout();

        cut.Find("img[title='Version 1.0.0-test']").Should().NotBeNull();
    }

    [Test]
    public void Renders_WifiOffIcon_WhenDisconnected()
    {
        _mockMqttClient.IsConnected.Returns(false);
        var cut = RenderLayout();

        cut.Markup.Should().Contain("Click to connect");
    }

    [Test]
    public void Renders_ReconnectingMessage_WhenStartedButNotConnected()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);

        var cut = RenderLayout();

        cut.Markup.Should().Contain("attempting to reconnect");
    }

    [Test]
    public async Task ReconnectStopButton_AwaitsStopAsync()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);
        var pending = new TaskCompletionSource();
        _mockMqttClient.StopAsync().Returns(pending.Task);

        var cut = RenderLayout();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Stop").Click();

        await _mockMqttClient.Received(1).StopAsync();
        pending.SetResult();
    }

    [Test]
    public void ReconnectStopButton_WhenStopFails_SurfacesErrorSnackbar()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);
        _mockMqttClient.StopAsync().Returns(Task.FromException(new InvalidOperationException("boom")));

        var cut = RenderLayout();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Stop").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Failed to stop"));
    }

    [Test]
    public void ChangePasswordRoute_IsTreatedAsAuthPage_ShowsBodyNotReconnect()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);
        Services.GetRequiredService<NavigationManager>().NavigateTo("change-password");

        var cut = RenderLayout();

        cut.Markup.Should().NotContain("attempting to reconnect");
    }

    [Test]
    public void NormalRoute_IsNotTreatedAsAuthPage_ShowsReconnect()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);

        var cut = RenderLayout();

        cut.Markup.Should().Contain("attempting to reconnect");
    }

    [Test]
    public async Task MainLayout_WhenDisposed_RemovesConnectionStateChangedHandler()
    {
        var cut = RenderLayout();

        await cut.Instance.DisposeAsync();

        _mockMqttClient.Received().ConnectionStateChangedAsync -= Arg.Any<Func<EventArgs, Task>>();
    }

    [Test]
    public async Task AppShellBar_WhenDisposed_RemovesConnectionStateChangedHandler()
    {
        EnsureMudProviders();
        var bar = RenderBar();

        await bar.Instance.DisposeAsync();

        _mockMqttClient.Received().ConnectionStateChangedAsync -= Arg.Any<Func<EventArgs, Task>>();
    }

    [Test]
    public async Task ConnectionToggle_WhenDisconnected_PassesSizingOptionsToConnectionDialog()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true); // prevent auto-open on first render

        var cut = RenderLayout();
        await Task.Delay(50);
        _mockDialogService.ClearReceivedCalls();

        var bar = cut.FindComponent<AppShellBar>();
        await bar.InvokeAsync(() => bar.Instance.ConnectionToggle());

        await _mockDialogService.Received(1).ShowAsync<ConnectionDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogOptions>(o => o.MaxWidth == MaxWidth.Small && o.FullWidth == true));
    }

    [Test]
    public void UiPreferencesChanged_UpdatesThemes()
    {
        var themes = new Themes();
        Services.AddSingleton<IThemes>(themes);
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { Theme = "dark", FontAccessible = false }
        };
        _mockConfig.Config.Returns(cfg);

        var cut = RenderLayout();

        cfg.Ui.Theme = "light";
        cfg.Ui.FontAccessible = true;
        _mockConfig.UiPreferencesChanged += Raise.Event<Action>();

        themes.IsDarkMode.Should().BeFalse();
        themes.IsFontAccessible.Should().BeTrue();
    }

    [Test]
    public void LogoutButton_IsNotRed()
    {
        AuthorizationContext.SetAuthorized("admin").SetRoles(AppRoles.Admin);
        _mockAppInfo.RequiresAuthentication.Returns(true);

        var cut = RenderLayout();

        var logout = cut.FindAll("button").First(b => b.TextContent.Contains("Logout"));
        logout.ClassList.Should().NotContain("mud-button-color-error");
    }

}
