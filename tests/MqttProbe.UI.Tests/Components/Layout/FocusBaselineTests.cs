namespace MqttProbe.UI.Tests.Components.Layout;

[TestFixture]
public class FocusBaselineTests
{
    private const string ComponentsCss = "src/MqttProbe.UI/wwwroot/css/components.css";
    private const string AppShellBarCss = "src/MqttProbe.UI/Components/Layout/AppShellBar.razor.css";
    private const string SettingsCss = "src/MqttProbe.UI/Components/Pages/Settings.razor.css";
    private const string PayloadBrowserCss = "src/MqttProbe.UI/Components/Browser/PayloadBrowser.razor.css";
    private const string MobileTopicBarCss = "src/MqttProbe.UI/Components/Browser/MobileTopicBar.razor.css";
    private const string MainLayoutMarkup = "src/MqttProbe.UI/Components/Layout/MainLayout.razor";
    private const string AppShellBarMarkup = "src/MqttProbe.UI/Components/Layout/AppShellBar.razor";

    [Test]
    public void SharedStylesheet_DrawsOneVisibleRingForKeyboardFocus()
    {
        var css = ReadRepositoryFile(ComponentsCss);

        css.Should().Contain(":focus-visible {");
        css.Should().Contain("outline: 2px solid var(--mud-palette-primary)");
        css.Should().Contain("a:focus-visible", "MudBlazor clears the link outline and outranks the shared rule");
        css.Should().Contain("button:focus-visible", "MudBlazor's `button:focus` clears the button outline at the same specificity");
    }

    [TestCase(AppShellBarCss)]
    [TestCase(SettingsCss)]
    [TestCase(PayloadBrowserCss)]
    [TestCase(MobileTopicBarCss)]
    public void ComponentStyles_KeepAVisibleKeyboardFocusRing(string cssPath)
    {
        var css = ReadRepositoryFile(cssPath);

        css.Should().NotContain("outline: none");
        css.Should().Contain("outline: 2px solid var(--mud-palette-primary)");
    }

    [Test]
    public void Dialogs_CloseOnEscape_WithoutBypassingTheDirtyFormGuard()
    {
        ReadRepositoryFile(MainLayoutMarkup).Should().Contain("CloseOnEscapeKey=\"true\"");
        ReadRepositoryFile(AppShellBarMarkup).Should().Contain("CloseOnEscapeKey = false",
            "the connection dialog keeps its own Escape handling so unsaved edits are guarded");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

        File.Exists(path).Should().BeTrue($"expected the repository file at '{path}' to exist");
        return File.ReadAllText(path);
    }
}
