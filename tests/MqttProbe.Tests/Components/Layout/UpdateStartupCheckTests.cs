using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Platform;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Layout;

[TestFixture]
public class UpdateStartupCheckTests : BunitTestContext
{
    private IUpdateService _updateService = null!;
    private ISnackbar _snackbar = null!;

    [SetUp]
    public void Setup()
    {
        _updateService = Substitute.For<IUpdateService>();
        _snackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_updateService);
        Services.AddSingleton(_snackbar);
    }

    [Test]
    public void DoesNotCheck_WhenUnsupported()
    {
        _updateService.IsSupported.Returns(false);

        Render<UpdateStartupCheck>();

        _updateService.DidNotReceive().CheckForUpdateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void ShowsSnackbar_WhenUpdateAvailable()
    {
        _updateService.IsSupported.Returns(true);
        _updateService.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns("1.2.0");

        var cut = Render<UpdateStartupCheck>();

        cut.WaitForAssertion(() => _snackbar.Received(1).Add(
            Arg.Is<string>(m => m.Contains("1.2.0")), Severity.Info, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>()));
    }

    [Test]
    public void ShowsNothing_WhenUpToDate()
    {
        _updateService.IsSupported.Returns(true);
        _updateService.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var cut = Render<UpdateStartupCheck>();

        cut.WaitForAssertion(() => _updateService.Received(1).CheckForUpdateAsync(Arg.Any<CancellationToken>()));
        _snackbar.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<Severity>(), Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }
}
