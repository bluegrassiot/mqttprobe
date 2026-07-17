using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Layout;
using MqttProbe.Components.Pages;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Pages;

[TestFixture]
public class SettingsUpdateSectionTests : BunitTestContext
{
    private ISettingsStore _mockStore = null!;
    private Themes _themes = null!;
    private IAppInfoService _mockAppInfo = null!;
    private IUpdateService _updateService = null!;

    [SetUp]
    public void Setup()
    {
        _mockStore = Substitute.For<ISettingsStore>();
        _mockStore.Config.Returns(new AppConfiguration
        {
            Ui = new UiPreferences { Theme = "dark", FontAccessible = false, AutoResubscribe = true },
            Performance = new PerformanceSettings { MaxStoredMessages = 10_000, MaxMessagesPerSecond = 50_000 }
        });
        _themes = new Themes();
        _mockAppInfo = Substitute.For<IAppInfoService>();
        _mockAppInfo.RequiresAuthentication.Returns(true);
        _updateService = Substitute.For<IUpdateService>();
        Services.AddSingleton(_mockStore);
        Services.AddSingleton<IThemes>(_themes);
        Services.AddSingleton(_mockAppInfo);
        Services.AddSingleton(_updateService);
        AuthorizationContext.SetAuthorized("admin").SetRoles(AppRoles.Admin);
        EnsureMudProviders();
    }

    [Test]
    public void UpdateControls_Hidden_WhenUnsupported()
    {
        _updateService.IsSupported.Returns(false);

        var cut = Render<Settings>();

        cut.FindAll("[data-testid=check-updates-button]").Should().BeEmpty();
    }

    [Test]
    public async Task CheckButton_ShowsUpToDate_WhenNoUpdate()
    {
        _updateService.IsSupported.Returns(true);
        _updateService.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var cut = Render<Settings>();
        cut.Find("[data-testid=check-updates-button]").Click();

        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Markup.Should().Contain("You're up to date");
    }

    [Test]
    public async Task CheckButton_ShowsApplyButton_WhenUpdateFound()
    {
        _updateService.IsSupported.Returns(true);
        _updateService.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns("1.2.0");

        var cut = Render<Settings>();
        cut.Find("[data-testid=check-updates-button]").Click();

        await cut.InvokeAsync(() => Task.CompletedTask);

        var applyButton = cut.Find("[data-testid=apply-update-button]");
        applyButton.TextContent.Should().Contain("1.2.0");

        applyButton.Click();

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _updateService.Received(1).DownloadAndApplyAsync(Arg.Any<CancellationToken>());
    }
}
