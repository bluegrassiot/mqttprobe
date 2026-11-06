namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class BrowserResponsiveLayoutTests
{
    private const string BrowserMarkup = "src/MqttProbe.UI/Components/Browser/BrowserPanel.razor";
    private const string BrowserCss = "src/MqttProbe.UI/Components/Browser/BrowserPanel.razor.css";
    private const string PayloadCss = "src/MqttProbe.UI/Components/Browser/PayloadBrowser.razor.css";
    private const string ToolbarCss = "src/MqttProbe.UI/Components/Browser/TopicBulkToolbar.razor.css";

    private const string DesktopSplitBoundary = "@media (max-width: 959.98px)";
    private const string PhoneBoundary = "@media (max-width: 599.98px)";

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
        layout.Should().NotContain("padding-bottom: 64px",
            "the bottom nav only exists below the phone boundary");
        css.Should().NotContain("browser-tablet-layout");
    }

    [Test]
    public void BrowserCss_ReservesTheBottomNavOnlyWhereTheNavExists()
    {
        ReadMediaBlockContaining(BrowserCss, PhoneBoundary, ".browser-mobile-payload {")
            .Should().Contain("padding-bottom: 64px");
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

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

        File.Exists(path).Should().BeTrue($"expected the repository file at '{path}' to exist");
        return File.ReadAllText(path);
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
