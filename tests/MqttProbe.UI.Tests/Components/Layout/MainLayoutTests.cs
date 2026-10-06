using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Components.Browser.Connection;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Layout;

[TestFixture]
public class MainLayoutTests : BunitTestContext
{
    private IMqttManagedClient _mockMqttClient = null!;
    private IMessageStoreManager _mockMsgStore = null!;
    private IDialogService _mockDialogService = null!;
    private IAppInfoService _mockAppInfo = null!;
    private ISessionState _mockSessionState = null!;
    private IUiSettings _mockConfig = null!;
    private IUpdateService _mockUpdateService = null!;
    private IConnectionSessionLifecycle _mockLifecycle = null!;

    [SetUp]
    public void SetupMocks()
    {
        _mockMqttClient = Substitute.For<IMqttManagedClient>();
        _mockMsgStore = Substitute.For<IMessageStoreManager>();
        _mockDialogService = Substitute.For<IDialogService>();
        _mockAppInfo = Substitute.For<IAppInfoService>();
        _mockSessionState = Substitute.For<ISessionState>();
        _mockConfig = Substitute.For<IUiSettings>();
        var cfg = new AppConfiguration();
        _mockConfig.Ui.Returns(cfg.Ui);
        Substitute.For<IJSRuntime>();
        _mockUpdateService = Substitute.For<IUpdateService>();
        _mockUpdateService.IsSupported.Returns(false);

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
        Services.AddUiSettings(_mockConfig);
        Services.AddSingleton(_mockUpdateService);
        _mockLifecycle = Substitute.For<IConnectionSessionLifecycle>();
        _mockLifecycle.StopActiveConnectionAsync().Returns(Task.CompletedTask);
        Services.AddSingleton(_mockLifecycle);
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

        // Register IFormatDisplayNames (MetricsStatsChip needs it)
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var registry = builder.Build();
        var pipeline = new PayloadPipeline(registry, Substitute.For<Microsoft.Extensions.Logging.ILogger<PayloadPipeline>>());
        Services.AddSingleton(pipeline);
        Services.AddSingleton<IFormatDisplayNames>(new FormatDisplayNames(pipeline));
    }

    private IRenderedComponent<MainLayout> RenderLayout(string? bodyMarker = null)
    {
        return Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(builder =>
        {
            if (bodyMarker is not null)
                builder.AddContent(0, bodyMarker);
        })));
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
    public async Task ConnectionToggle_WhenConnected_CallsStopActiveConnectionAsync()
    {
        _mockMqttClient.IsConnected.Returns(true);

        var cut = RenderLayout();
        var bar = cut.FindComponent<AppShellBar>();

        await bar.InvokeAsync(() => bar.Instance.ConnectionToggle());

        await _mockLifecycle.Received(1).StopActiveConnectionAsync();
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

        cut.Markup.Should().Contain("Connect");
    }

    [Test]
    public void WhenStartedButNotConnected_StillRendersBody_NotReconnectPanel()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);

        const string marker = "body-content-marker-xyz";
        var cut = RenderLayout(marker);

        cut.Markup.Should().Contain(marker);
        cut.Markup.Should().NotContain("attempting to reconnect");
    }

    [Test]
    public void WhenDisconnected_DoesNotShowReconnectPanel()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);
        Services.GetRequiredService<NavigationManager>().NavigateTo("change-password");

        const string marker = "body-content-marker-xyz";
        var cut = RenderLayout(marker);

        cut.Markup.Should().Contain(marker);
        cut.Markup.Should().NotContain("attempting to reconnect");
    }

    [Test]
    public void NormalRoute_WhenDisconnected_StillRendersBody()
    {
        _mockMqttClient.IsConnected.Returns(false);
        _mockMqttClient.IsStarted.Returns(true);

        const string marker = "body-content-marker-xyz";
        var cut = RenderLayout(marker);

        cut.Markup.Should().Contain(marker);
        cut.Markup.Should().NotContain("attempting to reconnect");
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
            Arg.Is<DialogOptions>(o => o!.MaxWidth == MaxWidth.Small && o.FullWidth == true));
    }

    [Test]
    public void UiPreferencesChanged_UpdatesThemes()
    {
        var themes = new Themes();
        Services.AddSingleton<IThemes>(themes);
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { Theme = "dark", FontProfile = FontProfiles.Standard }
        };
        _mockConfig.Ui.Returns(cfg.Ui);

        RenderLayout();

        cfg.Ui.Theme = "light";
        cfg.Ui.FontProfile = FontProfiles.Accessible;
        _mockConfig.UiPreferencesChanged += Raise.Event<Action>();

        themes.IsDarkMode.Should().BeFalse();
        themes.FontProfile.Should().Be(FontProfiles.Accessible);
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

    [Test]
    public async Task OnAfterRenderAsync_FirstRender_AccessibleProfile_InvokesJsToggleTrue()
    {
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { FontProfile = FontProfiles.Accessible }
        };
        _mockConfig.Ui.Returns(cfg.Ui);

        JSInterop.SetupVoid("eval", _ => true);

        RenderLayout();
        await Task.Delay(50);

        var invocation = JSInterop.VerifyInvoke("eval");
        invocation.Arguments[0]!.ToString()!.Should().Contain("classList.toggle('font-accessible', true)");
    }

    [Test]
    public async Task OnAfterRenderAsync_FirstRender_StandardProfile_InvokesJsToggleFalse()
    {
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { FontProfile = FontProfiles.Standard }
        };
        _mockConfig.Ui.Returns(cfg.Ui);

        JSInterop.SetupVoid("eval", _ => true);

        RenderLayout();
        await Task.Delay(50);

        var invocation = JSInterop.VerifyInvoke("eval");
        invocation.Arguments[0]!.ToString()!.Should().Contain("classList.toggle('font-accessible', false)");
    }

    [Test]
    public async Task FontModeChanged_AfterRender_CallsSetFontProfileAsync()
    {
        var themes = new Themes();
        Services.AddSingleton<IThemes>(themes);
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { FontProfile = FontProfiles.Standard }
        };
        _mockConfig.Ui.Returns(cfg.Ui);
        var capturedProfile = (string?)null;
        _mockConfig.SetFontProfileAsync(Arg.Do<string>(p => capturedProfile = p))
            .Returns(Task.CompletedTask);

        RenderLayout();
        await Task.Delay(50);

        themes.SetFontProfile(FontProfiles.Accessible);

        capturedProfile.Should().Be(FontProfiles.Accessible);
    }

}
