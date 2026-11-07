namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class BrowserResponsiveLayoutTests
{
    private const string BrowserMarkup = "src/MqttProbe.UI/Components/Browser/BrowserPanel.razor";
    private const string BrowserCss = "src/MqttProbe.UI/Components/Browser/BrowserPanel.razor.css";
    private const string PayloadCss = "src/MqttProbe.UI/Components/Browser/PayloadBrowser.razor.css";
    private const string ToolbarCss = "src/MqttProbe.UI/Components/Browser/TopicBulkToolbar.razor.css";
    private const string PickerCss = "src/MqttProbe.UI/Components/Browser/TopicPickerOverlay.razor.css";

    private const string DesktopSplitBoundary = "@media (max-width: 959.98px)";
    private const string CompactNavBoundary =
        "@media (max-width: 599.98px), (max-width: 959.98px) and (max-height: 799.98px)";

    [Test]
    public void Markup_HasOnlyTheDesktopSplitAndTheMobileLayout()
    {
        var markup = ReadRepositoryFile(BrowserMarkup);

        markup.Should().Contain("<MudHidden Breakpoint=\"Breakpoint.SmAndDown\">");
        markup.Should().Contain("<MudHidden Breakpoint=\"Breakpoint.MdAndUp\">");
        markup.Should().NotContain("browser-tablet-layout");
        markup.Should().NotContain("Horizontal=\"true\"");
    }

    [Test]
    public void BrowserCss_ShowsMobileLayoutBelowTheDesktopSplitBoundary()
    {
        var css = ReadRepositoryFile(BrowserCss);

        var layout = ReadMediaBlockContaining(BrowserCss, DesktopSplitBoundary, ".browser-mobile-layout {");
        layout.Should().Contain("display: flex");
        layout.Should().NotContain("--app-mobile-nav-reserve",
            "the bottom nav only exists on phones and short foldables");
        layout.Should().NotContain("padding-bottom: 64px",
            "the reserve is the shell's dynamic token, not a fixed height");
        css.Should().NotContain("browser-tablet-layout");
    }

    [Test]
    public void BrowserCss_ReservesTheBottomNavOnlyWhereTheNavExists()
    {
        var reserve = ReadMediaBlockContaining(
            BrowserCss, CompactNavBoundary, ".browser-mobile-payload {");

        reserve.Should().Contain("padding-bottom: var(--app-mobile-nav-reserve)");
        reserve.Should().NotContain("padding-bottom: 64px",
            "the reserve follows the shell's safe-area-aware token");
    }

    [Test]
    public void PayloadCss_UsesMobileRowsBelowTheDesktopSplitBoundary()
    {
        ReadMediaBlockContaining(PayloadCss, DesktopSplitBoundary, ".payload-row {")
            .Should().Contain("display: flex");
    }

    [Test]
    public void PayloadCss_KeepsTheDesktopTopicColumnReadableWithLongPayloads()
    {
        ReadMediaBlockContaining(PayloadCss, "@media (min-width: 960px)", ".pb-cell-topic {")
            .Should().Contain("min-width: 160px");
    }

    [Test]
    public void ToolbarCss_UsesCompactToolbarBelowTheDesktopSplitBoundary()
    {
        ReadMediaBlockContaining(ToolbarCss, DesktopSplitBoundary, ".topic-bulk-toolbar__mobile {")
            .Should().Contain("display: flex");
    }

    [Test]
    public void ToolbarCss_GivesTheSelectionLabelTheOnlyFlexibleTrack()
    {
        var css = ReadRepositoryFile(ToolbarCss);

        ReadRuleContaining(ToolbarCss, ".topic-bulk-toolbar__mobile-count {")
            .Should().Contain("flex: 1 1 0");
        ReadRuleContaining(ToolbarCss, ".topic-bulk-toolbar__mobile-actions {")
            .Should().Contain("flex: 0 0 auto");
        ReadRuleContaining(ToolbarCss, ".topic-bulk-toolbar__mobile-actions .app-mobile-bar-btn {")
            .Should().Contain("white-space: nowrap");
        ReadMediaBlockContaining(ToolbarCss, "@media (max-width: 359.98px)", ".topic-bulk-toolbar__mobile-actions {")
            .Should().Contain("gap: 4px");
        css.Should().Contain("min-width: 0");
    }

    [Test]
    public void PickerCss_FillsTheViewportWithOneTreeScrollOwner()
    {
        var panel = ReadRuleContaining(PickerCss, ".topic-picker-overlay__panel {");
        panel.Should().Contain("width: 100%");
        panel.Should().Contain("height: 100dvh");
        panel.Should().Contain("max-width: none");
        panel.Should().Contain("max-height: 100dvh");
        panel.Should().Contain("border-radius: 0");
        panel.Should().Contain("env(safe-area-inset-top)");
        panel.Should().Contain("env(safe-area-inset-bottom)");

        ReadRuleContaining(PickerCss, ".topic-picker-overlay__body {")
            .Should().Contain("overflow: hidden");
        ReadRuleContaining(PickerCss, ".topic-picker-overlay__footer {")
            .Should().Contain("flex: 0 0 auto")
            .And.Contain("border-top: 1px solid var(--mud-palette-divider)");
        ReadRuleContaining(PickerCss, ".topic-picker-overlay__done {")
            .Should().Contain("min-height: 48px");
        ReadRuleContaining(PickerCss, ".topic-browser-panel {")
            .Should().Contain("flex: 1 1 auto").And.Contain("min-height: 0");
        ReadRuleContaining(PickerCss, ".topic-header-row {")
            .Should().Contain("justify-content: flex-end");
        ReadRuleContaining(PickerCss, ".topic-header-row > :not(.topic-icon-key-toggle) {")
            .Should().Contain("display: none");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

        File.Exists(path).Should().BeTrue($"expected the repository file at '{path}' to exist");
        return File.ReadAllText(path);
    }

    private static string ReadRuleContaining(string cssFilePath, string selector)
    {
        var css = ReadRepositoryFile(cssFilePath);
        var index = css.IndexOf(selector, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0,
            $"expected '{selector}' in {cssFilePath}");
        return ExtractBracedBlockFromFirstBrace(css[index..]);
    }

    private static string ReadMediaBlockContaining(string cssFilePath, string mediaQuery, string anchor)
    {
        var css = ReadRepositoryFile(cssFilePath);
        var header = mediaQuery + " {";

        var start = 0;
        while ((start = css.IndexOf(header, start, StringComparison.Ordinal)) >= 0)
        {
            var block = ExtractBracedBlockFromFirstBrace(css[start..]);
            if (block.Contains(anchor, StringComparison.Ordinal))
                return block;

            start += block.Length;
        }

        throw new InvalidOperationException(
            $"expected a '{header}' block in {cssFilePath} containing '{anchor}'");
    }

    private static string ExtractBracedBlockFromFirstBrace(string text)
    {
        var open = text.IndexOf('{');
        open.Should().BeGreaterThanOrEqualTo(0, "expected a '{' to open the block");

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
                return text[..(i + 1)];
        }

        throw new InvalidOperationException("unbalanced braces in the extracted CSS block");
    }
}
