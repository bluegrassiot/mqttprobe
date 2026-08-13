using MqttProbe.Core.Models.Configuration;
using MqttProbe.UI.Components.Layout;

namespace MqttProbe.UI.Tests.Components.Layout;

[TestFixture]
public class ThemesTests
{
    [Test]
    public void IsDarkMode_DefaultsToTrue()
    {
        var themes = new Themes();

        themes.IsDarkMode.Should().BeTrue();
    }

    [Test]
    public void ToggleMode_FromDark_SwitchesToLight()
    {
        var themes = new Themes();

        themes.ToggleMode();

        themes.IsDarkMode.Should().BeFalse();
    }

    [Test]
    public void ToggleMode_ToggledTwice_ReturnsToDarkMode()
    {
        var themes = new Themes();

        themes.ToggleMode();
        themes.ToggleMode();

        themes.IsDarkMode.Should().BeTrue();
    }

    [Test]
    public void ToggleMode_FiresModeChanged()
    {
        var themes = new Themes();
        var fired = false;
        themes.ModeChanged += () => fired = true;

        themes.ToggleMode();

        fired.Should().BeTrue();
    }

    [Test]
    public void ToggleMode_FiresModeChanged_OnEachCall()
    {
        var themes = new Themes();
        var count = 0;
        themes.ModeChanged += () => count++;

        themes.ToggleMode();
        themes.ToggleMode();
        themes.ToggleMode();

        count.Should().Be(3);
    }

    [Test]
    public void ModeChanged_IsNotFiredOnConstruction()
    {
        var fired = false;

        var themes = new Themes();
        themes.ModeChanged += () => fired = true;

        fired.Should().BeFalse();
    }

    [Test]
    public void DarkLightModeButtonIcon_WhenDark_ReturnsSunIcon()
    {
        var themes = new Themes(); // dark by default

        themes.DarkLightModeButtonIcon.Should().Be(LucideIcons.Sun);
    }

    [Test]
    public void DarkLightModeButtonIcon_WhenLight_ReturnsMoonIcon()
    {
        var themes = new Themes();
        themes.ToggleMode(); // switch to light

        themes.DarkLightModeButtonIcon.Should().Be(LucideIcons.Moon);
    }

    [Test]
    public void CurrentTheme_IsNotNull()
    {
        var themes = new Themes();

        themes.CurrentTheme.Should().NotBeNull();
    }

    [Test]
    public void CurrentTheme_HasBothPalettes()
    {
        var themes = new Themes();

        themes.CurrentTheme.PaletteLight.Should().NotBeNull();
        themes.CurrentTheme.PaletteDark.Should().NotBeNull();
    }

    [Test]
    public void SetTheme_ToSameValue_DoesNotFireModeChanged()
    {
        var themes = new Themes();
        var fired = false;
        themes.ModeChanged += () => fired = true;

        themes.SetTheme(themes.IsDarkMode);

        fired.Should().BeFalse();
    }

    [Test]
    public void SetTheme_ToDifferentValue_FiresModeChanged()
    {
        var themes = new Themes();
        var fired = false;
        themes.ModeChanged += () => fired = true;

        var original = themes.IsDarkMode;
        themes.SetTheme(!original);

        fired.Should().BeTrue();
        themes.IsDarkMode.Should().Be(!original);
    }

    [Test]
    public void FontProfile_DefaultsToStandard()
    {
        var themes = new Themes();

        themes.FontProfile.Should().Be(FontProfiles.Standard);
    }

    [Test]
    public void DefaultTypography_UsesInterFont()
    {
        var themes = new Themes();

        themes.CurrentTheme.Typography.Default.FontFamily.Should().Contain("Inter");
    }

    [Test]
    public void SetFontProfile_Accessible_UsesOpenDyslexicTypography()
    {
        var themes = new Themes();

        themes.SetFontProfile(FontProfiles.Accessible);

        themes.FontProfile.Should().Be(FontProfiles.Accessible);
        themes.CurrentTheme.Typography.Default.FontFamily.Should().Contain("OpenDyslexic");
        themes.CurrentTheme.Typography.H1.FontFamily.Should().Contain("OpenDyslexic");
        themes.CurrentTheme.Typography.H6.FontFamily.Should().Contain("OpenDyslexic");
    }

    [Test]
    public void SetFontProfile_Standard_UsesInterAndChakraPetch()
    {
        var themes = new Themes();
        themes.SetFontProfile(FontProfiles.Accessible);

        themes.SetFontProfile(FontProfiles.Standard);

        themes.FontProfile.Should().Be(FontProfiles.Standard);
        themes.CurrentTheme.Typography.Default.FontFamily.Should().Contain("Inter");
        themes.CurrentTheme.Typography.H1.FontFamily.Should().Contain("Chakra Petch");
    }

    [Test]
    public void SetFontProfile_FiresFontModeChangedOnlyOnChange()
    {
        var themes = new Themes();
        themes.SetFontProfile(FontProfiles.Standard); // normalize
        var count = 0;
        themes.FontModeChanged += () => count++;

        themes.SetFontProfile(FontProfiles.Standard); // no-op
        themes.SetFontProfile(FontProfiles.Accessible); // fires
        themes.SetFontProfile(FontProfiles.Accessible); // no-op

        count.Should().Be(1);
    }

    [Test]
    public void SetFontProfile_InvalidValue_NormalizesToStandard()
    {
        var themes = new Themes();
        themes.SetFontProfile("garbage");

        themes.FontProfile.Should().Be(FontProfiles.Standard);
    }
}
