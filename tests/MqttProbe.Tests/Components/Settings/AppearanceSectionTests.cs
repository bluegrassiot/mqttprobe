using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Layout;
using MqttProbe.Components.Settings;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Settings;

[TestFixture]
public class AppearanceSectionTests : BunitTestContext
{
    private ISettingsStore _mockStore = null!;
    private Themes _themes = null!;

    [SetUp]
    public void Setup()
    {
        _mockStore = Substitute.For<ISettingsStore>();
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { Theme = "dark", FontAccessible = false }
        };
        _mockStore.Config.Returns(cfg);
        _mockStore.Ui.Returns(cfg.Ui);
        _themes = new Themes();
        Services.AddSettingsSubstitute(_mockStore);
        Services.AddSingleton<IThemes>(_themes);
        EnsureMudProviders();
    }

    [Test]
    public void Theme_AppliesToLiveThemes()
    {
        var cut = Render<AppearanceSection>();

        var themeSelect = cut.FindComponents<MudSelect<string>>()
            .First(s => s.Instance.Label == "Theme");
        cut.InvokeAsync(() => themeSelect.Instance.ValueChanged.InvokeAsync("light"));

        _themes.IsDarkMode.Should().BeFalse();
    }

    [Test]
    public void Font_AppliesToLiveThemes()
    {
        var cut = Render<AppearanceSection>();

        var fontSelect = cut.FindComponents<MudSelect<string>>()
            .First(s => s.Instance.Label == "Font");
        cut.InvokeAsync(() => fontSelect.Instance.ValueChanged.InvokeAsync("open-dyslexic"));

        _themes.IsFontAccessible.Should().BeTrue();
    }

    [Test]
    public void Section_RendersNoPanelChrome()
    {
        var cut = Render<AppearanceSection>();

        cut.FindAll(".app-chrome-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-typography-h6");
    }
}
