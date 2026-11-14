using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesMobileDetailTests : BunitTestContext
{
    private const string NodesCss = "src/MqttProbe.UI/Components/Sparkplug/SparkplugNodesView.razor.css";
    private const string ComponentsCss = "src/MqttProbe.UI/wwwroot/css/components.css";
    private const string CompactQuery =
        "@media (max-width: 599.98px), (max-width: 959.98px) and (max-height: 799.98px)";

    [SetUp]
    public void Setup()
    {
        var group = new SpbGroup();
        group.Nodes["edge-01"] = new SpbNode
        {
            NodeId = "edge-01",
            GroupId = "factory",
            Status = SpbNodeStatus.Online
        };

        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup> { ["factory"] = group });
        Services.AddSingleton(topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(sparkplugSettings);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        EnsureMudProviders();
    }

    [Test]
    public void SelectedNode_RendersAPinnedBarAboveTheScrollingDetailBody()
    {
        var cut = Render<SparkplugNodesView>();
        cut.FindAll(".spb-detail-bar").Should().BeEmpty("the bar is inert until a node is selected");
        cut.Find(".spb-nodes-shell").ClassList.Should().NotContain("spb-nodes-shell--detail-open");

        cut.Find(".spb-node-row button.spb-node-select").Click();

        cut.Find(".spb-nodes-shell").ClassList.Should().Contain("spb-nodes-shell--detail-open");

        var bar = cut.Find(".spb-detail-bar");
        bar.QuerySelector("button.spb-detail-bar__back").Should().NotBeNull(
            "the pinned bar carries the takeover's Back control");
        bar.QuerySelector(".spb-detail-bar__title")!.TextContent.Trim().Should().Be("edge-01",
            "the node stays named while the body scrolls");

        var detail = cut.Find(".spb-split__detail");
        detail.Children.Select(child => child.ClassName).Should()
            .ContainInOrder("spb-detail-bar", "spb-detail-body");

        cut.Find(".spb-detail-body .spb-detail-panel").Should().NotBeNull(
            "the detail paper scrolls inside the takeover body");
    }

    [Test]
    public void DetailBar_Back_ClearsSelectionAndClosesTheTakeover()
    {
        var cut = Render<SparkplugNodesView>();
        cut.Find(".spb-node-row button.spb-node-select").Click();

        cut.Find("button.spb-detail-bar__back").Click();

        cut.FindAll(".spb-split--detail-open").Should().BeEmpty();
        cut.FindAll(".spb-detail-bar").Should().BeEmpty();
        cut.FindAll(".spb-detail-body .spb-detail-panel").Should().BeEmpty();
    }

    [Test]
    public void DesktopSplit_KeepsTheDetailPaneAndDoesNotRenderTheMobileBar()
    {
        var wrapper = Render<CascadingValue<Breakpoint>>(parameters => parameters
            .Add(value => value.Value, Breakpoint.Lg)
            .Add(value => value.IsFixed, true)
            .Add(value => value.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<SparkplugNodesView>(0);
                builder.CloseComponent();
            })));

        var cut = wrapper.FindComponent<SparkplugNodesView>();
        cut.Find(".spb-node-row button.spb-node-select").Click();

        cut.FindAll(".spb-detail-bar").Should().BeEmpty("desktop keeps its split-pane detail");
        cut.FindAll(".spb-detail-body").Should().BeEmpty();
        cut.FindAll(".spb-split__detail .spb-detail-panel").Should().ContainSingle();
    }

    [Test]
    public void CompactCss_FillsTheViewportWithOneScrollOwner()
    {
        var block = ReadMediaBlockContaining(NodesCss, CompactQuery, ".spb-split--detail-open .spb-split__detail {");

        var takeover = ReadRule(block, ".spb-split--detail-open .spb-split__detail {");
        takeover.Should().Contain("position: fixed");
        takeover.Should().Contain("inset: 0");
        takeover.Should().Contain("overflow: hidden");
        takeover.Should().Contain("background: var(--mud-palette-surface)");
        takeover.Should().Contain("padding: 0");
        takeover.Should().Contain("z-index: calc(var(--mud-zindex-appbar) + 1)",
            "the takeover must cover the app bar (1300) and stay under the 1400 dialog layer");

        var bar = ReadRule(block, ".spb-split--detail-open .spb-detail-bar {");
        bar.Should().Contain("display: flex");
        bar.Should().Contain("env(safe-area-inset-top, 0px)");
        bar.Should().Contain("env(safe-area-inset-right, 0px)");
        bar.Should().Contain("env(safe-area-inset-left, 0px)");
        bar.Should().Contain("border-bottom: 1px solid var(--mud-palette-divider)");

        var body = ReadRule(block, ".spb-split--detail-open .spb-detail-body {");
        body.Should().Contain("flex: 1 1 auto");
        body.Should().Contain("min-height: 0");
        body.Should().Contain("overflow-y: auto");
        body.Should().Contain("overscroll-behavior: contain");
        body.Should().Contain("env(safe-area-inset-bottom, 0px)");

        ReadRule(block, ".spb-split--detail-open .spb-split__detail ::deep .spb-detail-back {")
            .Should().Contain("display: none",
                "the pinned bar must be the only Back control in the takeover");
    }

    [Test]
    public void CompactCss_HidesTheNodesHeaderBehindTheTakeover()
    {
        var block = ReadMediaBlockContaining(
            NodesCss, CompactQuery, ".spb-nodes-shell--detail-open .spb-nodes-header {");

        ReadRule(block, ".spb-nodes-shell--detail-open .spb-nodes-header {")
            .Should().Contain("display: none");
    }

    [Test]
    public void BaseCss_LeavesStackedTabletsWithTheInlineBackAndPaneScroll()
    {
        var css = ReadRepositoryFile(NodesCss);

        ReadRule(css, ".spb-detail-bar {").Should().Contain("display: none");
        ReadRule(css, ".spb-detail-body {").Should().Contain("display: contents",
            "the wrapper must not add a second layout box outside the takeover");
    }

    [Test]
    public void CompactChrome_LeavesTheTabOrderOnlyWhileTheTakeoverIsOpen()
    {
        var block = ReadMediaBlockContaining(
            ComponentsCss, CompactQuery, "body:has(.mud-tab-panel-active .spb-nodes-shell--detail-open)");

        block.Should().Contain(
            "body:has(.mud-tab-panel-active .spb-nodes-shell--detail-open) .app-shell-appbar",
            "the covered app bar must leave the tab order with the nav");
        block.Should().Contain("visibility: hidden");
        block.Should().NotContain("display: none",
            "the shell chrome keeps its layout box so the takeover restores it in place");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

        File.Exists(path).Should().BeTrue($"expected the repository file at '{path}' to exist");
        return File.ReadAllText(path);
    }

    private static string ReadRule(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected '{selector}' in the scoped stylesheet");
        return ExtractBracedBlock(css[start..]);
    }

    private static string ReadMediaBlockContaining(string cssFilePath, string mediaQuery, string anchor)
    {
        var css = ReadRepositoryFile(cssFilePath);
        var header = mediaQuery + " {";

        var start = 0;
        while ((start = css.IndexOf(header, start, StringComparison.Ordinal)) >= 0)
        {
            var block = ExtractBracedBlock(css[start..]);
            if (block.Contains(anchor, StringComparison.Ordinal))
                return block;

            start += block.Length;
        }

        throw new InvalidOperationException(
            $"expected a '{header}' block in {cssFilePath} containing '{anchor}'");
    }

    private static string ExtractBracedBlock(string text)
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
