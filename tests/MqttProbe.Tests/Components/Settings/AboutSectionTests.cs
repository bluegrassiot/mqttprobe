using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Settings;
using MqttProbe.Services.Platform;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Settings;

[TestFixture]
public class AboutSectionTests : BunitTestContext
{
    private IAppInfoService _mockAppInfo = null!;
    private IUpdateService _updateService = null!;

    [SetUp]
    public void Setup()
    {
        _mockAppInfo = Substitute.For<IAppInfoService>();
        _updateService = Substitute.For<IUpdateService>();
        Services.AddSingleton(_mockAppInfo);
        Services.AddSingleton(_updateService);
        EnsureMudProviders();
    }

    [Test]
    public void ShowsVersion()
    {
        _mockAppInfo.GetVersion().Returns("1.0.0-test");

        var cut = Render<AboutSection>();

        cut.Markup.Should().Contain("Version 1.0.0-test");
    }

    [Test]
    public void UpdateControls_Hidden_WhenUnsupported()
    {
        _updateService.IsSupported.Returns(false);

        var cut = Render<AboutSection>();

        cut.FindAll("[data-testid=check-updates-button]").Should().BeEmpty();
    }

    [Test]
    public async Task CheckButton_ShowsUpToDate_WhenNoUpdate()
    {
        _updateService.IsSupported.Returns(true);
        _updateService.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var cut = Render<AboutSection>();
        cut.Find("[data-testid=check-updates-button]").Click();

        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Markup.Should().Contain("You're up to date");
    }

    [Test]
    public async Task CheckButton_ShowsApplyButton_WhenUpdateFound()
    {
        _updateService.IsSupported.Returns(true);
        _updateService.CheckForUpdateAsync(Arg.Any<CancellationToken>()).Returns("1.2.0");

        var cut = Render<AboutSection>();
        cut.Find("[data-testid=check-updates-button]").Click();

        await cut.InvokeAsync(() => Task.CompletedTask);

        var applyButton = cut.Find("[data-testid=apply-update-button]");
        applyButton.TextContent.Should().Contain("1.2.0");

        applyButton.Click();

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _updateService.Received(1).DownloadAndApplyAsync(Arg.Any<CancellationToken>());
    }
}
