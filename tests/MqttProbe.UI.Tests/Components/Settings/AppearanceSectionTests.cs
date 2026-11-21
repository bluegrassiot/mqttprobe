using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.UI.Components.Layout;
using MqttProbe.UI.Components.Settings;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Extensions;

namespace MqttProbe.UI.Tests.Components.Settings;

[TestFixture]
public class AppearanceSectionTests : BunitTestContext
{
    private IUiSettings _mockStore = null!;
    private Themes _themes = null!;

    [SetUp]
    public void Setup()
    {
        _mockStore = Substitute.For<IUiSettings>();
        var cfg = new AppConfiguration
        {
            Ui = new UiPreferences { Theme = "dark", FontProfile = FontProfiles.Standard }
        };
        _mockStore.Ui.Returns(cfg.Ui);
        _themes = new Themes();
        Services.AddUiSettings(_mockStore);
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
            .First(s => s.Instance.Label == "Font profile");
        cut.InvokeAsync(() => fontSelect.Instance.ValueChanged.InvokeAsync(FontProfiles.Accessible));

        _themes.FontProfile.Should().Be(FontProfiles.Accessible);
    }

    [Test]
    public async Task MultiSelect_Toggle_CallsSetter()
    {
        _mockStore.SetTopicTreeMultiSelectAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);
        var cut = Render<AppearanceSection>();

        var toggle = cut.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Enable multi-select in the topic tree");
        await cut.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(false));

        await _mockStore.Received(1).SetTopicTreeMultiSelectAsync(false);
    }

    [Test]
    public void MultiSelect_Toggle_ReflectsSavedPreference()
    {
        _mockStore.Ui.TopicTreeMultiSelect = false;
        var rendered = Render<AppearanceSection>();
        rendered.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Enable multi-select in the topic tree")
            .Instance.GetState(x => x.Value).Should().BeFalse();

        _mockStore.Ui.TopicTreeMultiSelect = true;
        var restored = Render<AppearanceSection>();
        restored.FindComponents<MudSwitch<bool>>()
            .First(s => s.Instance.Label == "Enable multi-select in the topic tree")
            .Instance.GetState(x => x.Value).Should().BeTrue();
    }

    [Test]
    public void Helpers_UseTheSharedCaptionStyling()
    {
        var cut = Render<AppearanceSection>();

        cut.FindAll(".settings-caption").Should().HaveCount(2);
        cut.FindAll(".mud-input-helper-text").Should().BeEmpty(
            "the font-profile hint now uses the same caption treatment as multi-select");
    }

    [Test]
    public void Section_RendersNoPanelChrome()
    {
        var cut = Render<AppearanceSection>();

        cut.FindAll(".app-chrome-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("mud-typography-h6");
    }
}
