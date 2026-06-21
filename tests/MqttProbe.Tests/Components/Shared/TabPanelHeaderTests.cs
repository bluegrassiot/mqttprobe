using Microsoft.AspNetCore.Components;
using MqttProbe.Components.Layout;
using MqttProbe.Components.Shared;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Shared;

[TestFixture]
public class TabPanelHeaderTests : BunitTestContext
{
    [Test]
    public void Renders_Title_InHeaderRow()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Charts"));

        var row = cut.Find(".app-tabpanel-header__row");
        row.Should().NotBeNull("the header row must wrap the title");
        row.ClassList.Should().Contain("app-tabpanel-header__row");

        var title = cut.Find(".app-tabpanel-header__title");
        title.TextContent.Should().Contain("Charts",
            "the title text must render inside the title slot");
    }

    [Test]
    public void Renders_Icon_WhenProvided()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Charts")
            .Add(x => x.Icon, LucideIcons.Activity));

        var row = cut.Find(".app-tabpanel-header__row");
        row.QuerySelector("svg.mud-icon-root").Should().NotBeNull(
            "an Icon value should render a MudIcon SVG inside the header row");
    }

    [Test]
    public void Omits_Icon_WhenNotProvided()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Charts"));

        cut.FindAll(".app-tabpanel-header__row svg.mud-icon-root").Should().BeEmpty(
            "no icon SVG should be rendered when the Icon parameter is null");
    }

    [Test]
    public void Renders_HeaderChipsSlot_InsideChipsContainer()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Charts")
            .Add(x => x.HeaderChips, (RenderFragment)(b =>
                b.AddMarkupContent(0, "<span class=\"test-chip\">3 charts</span>"))));

        var chips = cut.Find(".app-tabpanel-header__chips");
        chips.ClassList.Should().Contain("app-tabpanel-header__chips");
        chips.QuerySelector(".test-chip").Should().NotBeNull(
            "the HeaderChips fragment should render inside the chips container");
    }

    [Test]
    public void Renders_HeaderActionsSlot_InsideActionsContainer()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Charts")
            .Add(x => x.HeaderActions, (RenderFragment)(b =>
                b.AddMarkupContent(0, "<button class=\"test-action\">Create</button>"))));

        var actions = cut.Find(".app-tabpanel-header__actions");
        actions.ClassList.Should().Contain("app-tabpanel-header__actions");
        actions.QuerySelector("button.test-action").Should().NotBeNull(
            "the HeaderActions fragment should render inside the actions container");
    }

    [Test]
    public void Renders_HeaderFiltersRow_WhenFiltersProvided()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Nodes")
            .Add(x => x.HeaderFilters, (RenderFragment)(b =>
                b.AddMarkupContent(0, "<input class=\"test-filter\" />"))));

        var filters = cut.Find(".app-tabpanel-header__filters");
        filters.ClassList.Should().Contain("app-tabpanel-header__filters");
        filters.QuerySelector("input.test-filter").Should().NotBeNull(
            "the HeaderFilters fragment should render inside the second row");
    }

    [Test]
    public void Omits_HeaderFiltersRow_WhenFiltersNotProvided()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Charts"));

        cut.FindAll(".app-tabpanel-header__filters").Should().BeEmpty(
            "no second row should be rendered when the HeaderFilters fragment is null");
    }

    [Test]
    public void Applies_SpecBorderAndMargin_ToRoot()
    {
        EnsureMudProviders();

        var cut = Render<TabPanelHeader>(p => p
            .Add(x => x.Title, "Charts"));

        var root = cut.Find("div.app-tabpanel-header");
        root.ClassList.Should().Contain("app-tabpanel-header",
            "the spec requires the root to use the app-tabpanel-header BEM class");

        var css = ReadScopedCss();
        css.Should().Contain("border-bottom: 1px",
            "the spec §3.1 requires a 1px slate divider below the header row");
        css.Should().Contain("margin-bottom: 24px",
            "the spec §3.1 requires 24px margin-bottom to separate header from body");
    }

    private static string ReadScopedCss()
    {
        var cssPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "MqttProbe.Shared", "Components", "Shared",
            "TabPanelHeader.razor.css"));

        File.Exists(cssPath).Should().BeTrue(
            $"expected the scoped stylesheet at '{cssPath}' to exist so the test can verify its rules");

        return File.ReadAllText(cssPath);
    }
}
