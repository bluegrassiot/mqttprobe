using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Components.Browser.Connection;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Layout;

[TestFixture]
public class ConnectionRequiredPanelTests : BunitTestContext
{
    private IMqttManagedClient _mqtt = null!;
    private IConnectionSessionLifecycle _lifecycle = null!;
    private IDialogService _dialogService = null!;
    private IRenderedComponent<MudSnackbarProvider> _snackbarProvider = null!;

    // Exercises the iOS branch from a non-iOS host.
    private sealed class IosConnectionRequiredPanel : ConnectionRequiredPanel
    {
        protected override bool IsIosHost => true;
    }

    [SetUp]
    public void Setup()
    {
        _mqtt = Substitute.For<IMqttManagedClient>();
        _lifecycle = Substitute.For<IConnectionSessionLifecycle>();
        _lifecycle.StopActiveConnectionAsync().Returns(Task.CompletedTask);
        _dialogService = Substitute.For<IDialogService>();

        Services.AddSingleton(_mqtt);
        Services.AddSingleton(_lifecycle);
        Services.AddSingleton(_dialogService);
        EnsureMudProviders();
        _snackbarProvider = Render<MudSnackbarProvider>();
    }

    [Test]
    public void WhenNotStarted_OnIos_Explains_The_Local_Network_Permission()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(false);

        var cut = Render<IosConnectionRequiredPanel>();

        var note = cut.Find(".connection-required__lan");
        note.TextContent.Should().Contain("iPhone apps need permission to reach devices on your Wi-Fi");
        // TN3179: iOS blocks rather than warns, so the copy must say how to recover.
        note.TextContent.Should().Contain("iOS Settings");
        cut.Find("[data-testid=connection-required-continue]").TextContent.Should().Contain("Continue");
    }

    [Test]
    public async Task WhenNotStarted_OnIos_Continue_Opens_The_Connection_Dialog()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(false);

        var cut = Render<IosConnectionRequiredPanel>();
        await cut.Find("[data-testid=connection-required-continue]").ClickAsync();

        await _dialogService.Received(1).ShowAsync<ConnectionDialog>(
            Arg.Is<string>(t => t == "Setup Connection"), Arg.Any<DialogOptions>());
    }

    [Test]
    public void WhenNotStarted_OffIos_Omits_The_Local_Network_Note()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(false);

        var cut = Render<ConnectionRequiredPanel>();

        cut.FindAll(".connection-required__lan").Should().BeEmpty();
        cut.FindAll("[data-testid=connection-required-continue]").Should().BeEmpty();
    }

    [Test]
    public void WhileReconnecting_OnIos_Omits_The_Local_Network_Note()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(true);

        var cut = Render<IosConnectionRequiredPanel>();

        cut.FindAll(".connection-required__lan").Should().BeEmpty();
    }

    [Test]
    public void WhenNotStarted_ShowsConnectPrompt()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(false);

        var cut = Render<ConnectionRequiredPanel>();

        cut.Markup.Should().Contain("Connect to a broker");
        cut.Markup.Should().NotContain("Attempting to reconnect");
        cut.FindAll(".signal-connecting").Should().BeEmpty();
        var alert = cut.FindComponent<MudAlert>();
        alert.Instance.Severity.Should().Be(Severity.Info);
        alert.Instance.Variant.Should().Be(Variant.Outlined);
        alert.Instance.Dense.Should().BeTrue();
        cut.FindAll("button").Should().BeEmpty();
    }

    [Test]
    public void WhenStartedButNotConnected_ShowsReconnectAndStop()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(true);

        var cut = Render<ConnectionRequiredPanel>();

        cut.Markup.Should().Contain("Attempting to reconnect");
        cut.FindAll(".signal-connecting").Should().ContainSingle();
        var alert = cut.FindComponent<MudAlert>();
        alert.Instance.Severity.Should().Be(Severity.Warning);
        alert.Instance.Variant.Should().Be(Variant.Outlined);
        alert.Instance.Dense.Should().BeTrue();
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Stop");
    }

    [Test]
    public async Task Stop_CallsStopActiveConnectionAsync()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(true);

        var cut = Render<ConnectionRequiredPanel>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Stop").Click();

        await _lifecycle.Received(1).StopActiveConnectionAsync();
    }

    [Test]
    public void Stop_WhenFails_SurfacesErrorSnackbar()
    {
        _mqtt.IsConnected.Returns(false);
        _mqtt.IsStarted.Returns(true);
        _lifecycle.StopActiveConnectionAsync()
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var cut = Render<ConnectionRequiredPanel>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Stop").Click();

        _snackbarProvider.WaitForAssertion(() =>
            _snackbarProvider.Markup.Should().Contain("Failed to stop"));
    }
}
