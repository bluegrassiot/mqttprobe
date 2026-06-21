using MqttProbe.Components.Layout;

namespace MqttProbe.Shared.Tests.Components.Layout;

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
    public void SetFontAccessible_True_EnablesAccessibleFont()
    {
        var themes = new Themes();

        themes.SetFontAccessible(true);

        themes.IsFontAccessible.Should().BeTrue();
    }

    [Test]
    public void SetFontAccessible_FiresFontModeChangedOnlyOnChange()
    {
        var themes = new Themes();
        themes.SetFontAccessible(false); // normalize
        var count = 0;
        themes.FontModeChanged += () => count++;

        themes.SetFontAccessible(false); // no-op
        themes.SetFontAccessible(true);  // fires
        themes.SetFontAccessible(true);  // no-op

        count.Should().Be(1);
    }
}
