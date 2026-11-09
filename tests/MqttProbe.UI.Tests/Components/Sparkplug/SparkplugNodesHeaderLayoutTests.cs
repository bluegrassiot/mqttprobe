namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesHeaderLayoutTests
{
    private const string Markup = "src/MqttProbe.UI/Components/Sparkplug/SparkplugNodesView.razor";
    private const string Css = "src/MqttProbe.UI/Components/Sparkplug/SparkplugNodesView.razor.css";

    [Test]
    public void Markup_WrapsTheSharedHeaderInTheNodesOwnedAnchor()
    {
        var markup = ReadRepositoryFile(Markup);

        var wrapperOpen = markup.IndexOf("<div class=\"spb-nodes-header\">", StringComparison.Ordinal);
        wrapperOpen.Should().BeGreaterThanOrEqualTo(0, "the Nodes header needs a native wrapper for CSS isolation");

        var headerOpen = markup.IndexOf("<TabPanelHeader", wrapperOpen, StringComparison.Ordinal);
        headerOpen.Should().BeGreaterThan(wrapperOpen, "the wrapper must contain the shared header component");

        var headerClose = markup.IndexOf("</TabPanelHeader>", headerOpen, StringComparison.Ordinal);
        var wrapperClose = markup.IndexOf("</div>", headerClose, StringComparison.Ordinal);
        headerClose.Should().BeGreaterThan(headerOpen);
        wrapperClose.Should().BeGreaterThan(headerClose, "the wrapper must close after the shared header");
    }

    [Test]
    public void Css_AnchorsEveryHeaderRuleOnTheNodesOwnedWrapper()
    {
        string[] headerSelectors =
        [
            "spb-count-chip",
            "spb-filter-select",
            "spb-filter-text",
            "spb-metric-view-toggle",
            "app-tabpanel-header__filters",
        ];

        var css = ReadRepositoryFile(Css);

        css.Should().Contain(".spb-nodes-header { display: contents; }",
            "the anchor must not introduce a layout box around the header");

        foreach (var selector in headerSelectors)
        {
            css.Should().Contain($".spb-nodes-header ::deep .{selector}",
                $"'{selector}' needs the Nodes wrapper in front of ::deep so the scoped selector can match");
            LeadingDeepRulesFor(css, selector).Should().BeEmpty(
                $"a leading ::deep for '{selector}' compiles to a scoped-attribute descendant selector with nothing to match");
        }
    }

    [Test]
    public void Css_KeepsTheFilterRowWrappingForPhoneWidths()
    {
        var css = ReadRepositoryFile(Css);

        var filters = ReadRule(css, ".spb-nodes-header ::deep .app-tabpanel-header__filters {");
        filters.Should().Contain("flex-wrap: wrap");
        filters.Should().Contain("row-gap: 6px");

        ReadRule(css, ".spb-nodes-header ::deep .spb-metric-view-toggle {")
            .Should().Contain("min-width: 168px");
    }

    private static IEnumerable<string> LeadingDeepRulesFor(string css, string selector) =>
        css.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("::deep ", StringComparison.Ordinal))
            .Where(line => line.Contains($".{selector}", StringComparison.Ordinal));

    private static string ReadRule(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected '{selector}' in the scoped stylesheet");

        var end = css.IndexOf('}', start);
        end.Should().BeGreaterThan(start);
        return css[start..end];
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

        File.Exists(path).Should().BeTrue($"expected the repository file at '{path}' to exist");
        return File.ReadAllText(path);
    }
}
