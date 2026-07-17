using Microsoft.Extensions.DependencyInjection;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Components.Layout;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Layout;

[TestFixture]
public class ConnectionRequiredPanelTests : BunitTestContext
{
    private IManagedMqttClient _mqtt = null!;
    private IConnectionSessionLifecycle _lifecycle = null!;
    private IRenderedComponent<MudSnackbarProvider> _snackbarProvider = null!;

    [SetUp]
    public void Setup()
    {
        _mqtt = Substitute.For<IManagedMqttClient>();
        _lifecycle = Substitute.For<IConnectionSessionLifecycle>();
        _lifecycle.StopActiveConnectionAsync().Returns(Task.CompletedTask);

        Services.AddSingleton(_mqtt);
        Services.AddSingleton(_lifecycle);
        EnsureMudProviders();
        _snackbarProvider = Render<MudSnackbarProvider>();
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
