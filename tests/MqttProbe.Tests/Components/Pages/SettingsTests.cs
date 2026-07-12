using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Layout;
using MqttProbe.Components.Pages;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Pages;

[TestFixture]
public class SettingsTests : BunitTestContext
{
    private ISettingsStore _mockStore = null!;
    private Themes _themes = null!;
    private IAppInfoService _mockAppInfo = null!;
    private IUpdateService _mockUpdateService = null!;

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
        _mockUpdateService = Substitute.For<IUpdateService>();
        Services.AddSingleton(_mockStore);
        Services.AddSingleton<IThemes>(_themes);
        Services.AddSingleton(_mockAppInfo);
        Services.AddSingleton(_mockUpdateService);
        AuthorizationContext.SetAuthorized("admin").SetRoles(AppRoles.Admin);
        EnsureMudProviders();
    }

    [Test]
    public void Theme_AppliesToLiveThemes()
    {
        var cut = Render<Settings>();

        var themeSelect = cut.FindComponents<MudSelect<string>>()
            .First(s => s.Instance.Label == "Theme");
        cut.InvokeAsync(() => themeSelect.Instance.ValueChanged.InvokeAsync("light"));

        _themes.IsDarkMode.Should().BeFalse();
    }

    [Test]
    public void Font_AppliesToLiveThemes()
    {
        var cut = Render<Settings>();

        var fontSelect = cut.FindComponents<MudSelect<string>>()
            .First(s => s.Instance.Label == "Font");
        cut.InvokeAsync(() => fontSelect.Instance.ValueChanged.InvokeAsync("open-dyslexic"));

        _themes.IsFontAccessible.Should().BeTrue();
    }

    [Test]
    public async Task MaxStoredMessages_Change_UsesNewSetter()
    {
        _mockStore.SetMaxStoredMessagesAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<Settings>();

        var numericFields = cut.FindComponents<MudNumericField<int>>();
        var maxStored = numericFields.First(f => f.Instance.Label == "Max stored messages");
        maxStored.Find("input").Change("5000");

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockStore.Received(1).SetMaxStoredMessagesAsync(5000);
    }

    [Test]
    public async Task MaxMessagesPerSecond_Change_UsesNewSetter()
    {
        _mockStore.SetMaxMessagesPerSecondAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<Settings>();

        var numericFields = cut.FindComponents<MudNumericField<int>>();
        var maxRate = numericFields.First(f => f.Instance.Label == "Max messages per second");
        maxRate.Find("input").Change("2000");

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockStore.Received(1).SetMaxMessagesPerSecondAsync(2000);
    }

    [Test]
    public void MaxDisplayedMessages_Field_Renders()
    {
        var cut = Render<Settings>();

        var numericFields = cut.FindComponents<MudNumericField<int>>();
        numericFields.Should().Contain(f => f.Instance.Label == "Max displayed messages");
    }

    [Test]
    public async Task MaxDisplayedMessages_Change_UsesNewSetter()
    {
        _mockStore.SetMaxDisplayMessagesAsync(Arg.Any<int>()).Returns(Task.CompletedTask);
        var cut = Render<Settings>();

        var numericFields = cut.FindComponents<MudNumericField<int>>();
        var maxDisplayed = numericFields.First(f => f.Instance.Label == "Max displayed messages");
        maxDisplayed.Find("input").Change("300");

        await cut.InvokeAsync(() => Task.CompletedTask);

        await _mockStore.Received(1).SetMaxDisplayMessagesAsync(300);
    }

    [Test]
    public void Account_ChangePassword_ButtonHasHref()
    {
        var cut = Render<Settings>();

        var link = cut.FindAll("a, button")
            .First(b => b.TextContent.Contains("Change password"));
        link.GetAttribute("href").Should().Be("/change-password");
    }

    [Test]
    public void Account_Section_Hidden_WhenRequiresAuthenticationIsFalse()
    {
        _mockAppInfo.RequiresAuthentication.Returns(false);
        var cut = Render<Settings>();

        cut.Markup.Should().NotContain("Account");
        cut.FindAll("a, button")
            .Should().NotContain(b => b.TextContent.Contains("Change password"));
    }

    [Test]
    public void About_ShowsVersion()
    {
        _mockAppInfo.GetVersion().Returns("1.0.0-test");
        var cut = Render<Settings>();

        cut.Markup.Should().Contain("Version 1.0.0-test");
    }

    [Test]
    public async Task EnrichSparkplugAliasNames_Toggle_CallsSetter()
    {
        _mockStore.SetEnrichSparkplugAliasNamesAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<Settings>();

        var switches = cut.FindComponents<MudSwitch<bool>>();
        var enrichSwitch = switches.First(s => s.Instance.Label == "Enrich Sparkplug alias names");
        await cut.InvokeAsync(() => enrichSwitch.Instance.ValueChanged.InvokeAsync(false));

        await _mockStore.Received(1).SetEnrichSparkplugAliasNamesAsync(false);
    }
}
