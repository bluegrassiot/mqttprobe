using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Services;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodeDetailsSectionTests : BunitTestContext
{
    private static readonly DateTime _birthUtc = new(2026, 10, 25, 0, 55, 7, 250, DateTimeKind.Utc);
    private static readonly DateTime _dataUtc = new(2026, 10, 25, 12, 30, 5, 125, DateTimeKind.Utc);
    private static readonly DateTime _deathUtc = new(2026, 10, 25, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _rebirthUtc = new(2026, 10, 25, 13, 5, 0, DateTimeKind.Utc);

    [SetUp]
    public void Setup() => EnsureMudProviders();

    [Test]
    public void Details_StartExpandedWithLastDataBeforeBirth()
    {
        var node = Node();
        node.LastBirthAt = _birthUtc;
        node.LastDataAt = _dataUtc;
        node.LastDeathAt = _deathUtc;
        node.LastRebirthRequestAt = _rebirthUtc;
        node.BirthDeathSequenceNumber = 4;

        var cut = RenderDetail(node);

        cut.Find(".spb-node-details .mud-expand-panel-header").TextContent.Should().Contain("Node details");
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();

        cut.FindAll(".spb-timestamps [data-time-value]")
            .Select(value => value.GetAttribute("data-time-value"))
            .Should().Equal(new[] { "node:last-data", "node:birth", "node:death", "node:rebirth-request" },
                "Last data leads, then Birth, Last NDEATH and Last rebirth request");

        var entries = cut.FindAll(".spb-timestamps > *");
        entries.Should().HaveCount(5);
        entries[entries.Count - 1].TextContent.Should().Contain("bdSeq");
    }

    [Test]
    public void Details_LabelTheNodeDeathAndTheRebirthRequest()
    {
        var node = Node();
        node.LastDeathAt = _deathUtc;
        node.LastRebirthRequestAt = _rebirthUtc;

        var text = RenderDetail(node).Find(".spb-timestamps").TextContent;

        text.Should().Contain("Last NDEATH").And.Contain("Last rebirth request");
        text.Should().NotContain("Rebirth requested");
    }

    [Test]
    public void Details_RenderOneAlignedLabelAndValueRowPerFact()
    {
        var node = Node();
        node.LastBirthAt = _birthUtc;
        node.LastDataAt = _dataUtc;
        node.LastDeathAt = _deathUtc;
        node.LastRebirthRequestAt = _rebirthUtc;
        node.BirthDeathSequenceNumber = 4;

        var cut = RenderDetail(node);

        var rows = cut.FindAll(".spb-timestamps > .spb-timestamp");
        rows.Should().HaveCount(5);
        rows.Select(row => row.QuerySelector(".spb-timestamp__label")!.TextContent.Trim())
            .Should().Equal(new[] { "Last data", "Birth", "Last NDEATH", "Last rebirth request", "bdSeq" });
        rows.Should().OnlyContain(row =>
            row.QuerySelectorAll(".spb-timestamp__label").Length == 1
            && row.QuerySelectorAll(".spb-timestamp__value").Length == 1);

        var bdSeq = rows[rows.Count - 1].QuerySelector("abbr")!;
        bdSeq.TextContent.Trim().Should().Be("bdSeq");
        bdSeq.GetAttribute("title").Should().Be("Birth/death session identifier",
            "the short label keeps the full name available to assistive tech");
    }

    [Test]
    public void Details_HealthySequenceOmitsTheExplanationButKeepsTheState()
    {
        var node = Node();
        node.Sequence = new SpbSequenceDiagnostics
        {
            State = SpbSequenceState.InOrder,
            ExpectedSequence = 8,
            ReceivedSequence = 7
        };

        var cut = RenderDetail(node);

        cut.Find(".spb-sequence-state").TextContent.Should().Be("In order");
        cut.FindAll(".spb-sequence-explanation").Should().BeEmpty(
            "an in-order sequence has no diagnostic to explain");
        cut.Find(".spb-sequence-values").TextContent.Should()
            .Contain("Next expected 8").And.Contain("Last received 7");
    }

    [Test]
    public void Details_ProblemSequenceKeepsExplanationAndCaveat()
    {
        var node = Node();
        node.Sequence = new SpbSequenceDiagnostics
        {
            State = SpbSequenceState.GapDetected,
            MissingSequences = [3, 4]
        };

        var cut = RenderDetail(node);

        cut.Find(".spb-sequence-state").TextContent.Should().Be("Unresolved gap");
        cut.Find(".spb-sequence-explanation").TextContent.Should().Contain("Unresolved gaps");
        cut.Find(".spb-sequence-caveat").TextContent.Should().Contain("A gap does not prove broker loss");
        cut.Find(".spb-sequence-values").TextContent.Should().Contain("Missing sequences 3-4");
    }

    [TestCase(SpbNodeStatus.Online)]
    [TestCase(SpbNodeStatus.Offline)]
    public void Details_ShowKnownNodeDeathWhateverTheCurrentStatus(SpbNodeStatus status)
    {
        var node = Node();
        node.Status = status;
        node.LastDeathAt = _deathUtc;

        var cut = RenderDetail(node);

        cut.FindAll("[data-time-value='node:death']").Should().ContainSingle(
            "a known NDEATH stays visible after the node comes back online");
        cut.Find(".spb-timestamps").TextContent.Should().Contain("Last NDEATH");
    }

    [Test]
    public void Details_OmitUnknownNodeDeath()
    {
        var cut = RenderDetail(Node());

        cut.FindAll("[data-time-value='node:death']").Should().BeEmpty();
    }

    [Test]
    public void Details_HeaderClickCollapsesWithoutHidingMetrics()
    {
        var node = Node();
        node.Metrics = [new SpbMetricSnapshot("Flow Rate", "double", "12.5000", _dataUtc)];

        var cut = RenderDetail(node);
        cut.Find(".mud-expand-panel-header").Click();

        cut.WaitForAssertion(() =>
            cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse());

        cut.FindAll("tr.spb-metric-row").Should().ContainSingle(
            "metrics live outside the collapsed Node details section");
        cut.Find(".mud-collapse-container").QuerySelector("tr.spb-metric-row").Should().BeNull();
        cut.Find(".spb-subsection-header").TextContent.Should().Contain("Metrics");
    }

    [TestCase("Enter")]
    [TestCase(" ")]
    public async Task Details_HeaderNativeActivationTogglesOnce(string key)
    {
        var cut = RenderDetail(Node());
        cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue();

        await cut.InvokeAsync(() => cut.Find(".mud-expand-panel-header").KeyDown(key));
        cut.WaitForAssertion(() =>
            cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeFalse());

        await cut.InvokeAsync(() => cut.Find(".mud-expand-panel-header").KeyDown(key));
        cut.WaitForAssertion(() =>
            cut.FindComponent<MudExpansionPanel>().Instance.Expanded.Should().BeTrue());
    }

    [Test]
    public void Details_ArrowKeysAreInterceptedOnThePanelHeader()
    {
        var cut = RenderDetail(Node());
        var rootId = cut.Find(".spb-node-details").Id!;

        var invocation = JSInterop.Invocations
            .Single(i => i.Identifier == "mudKeyInterceptor.connect"
                && i.Arguments.Count > 1 && Equals(i.Arguments[1], rootId));

        var options = invocation.Arguments[2].Should().BeOfType<KeyInterceptorOptions>().Subject;
        options.TargetClass.Should().Be("mud-expand-panel-header");
        options.Keys.Select(k => k.Key).Should().BeEquivalentTo(new[] { "ArrowRight", "ArrowLeft" });
        options.Keys.Should().OnlyContain(k => k.SubscribeDown && k.IgnoreDownRepeats
            && k.PreventDown == "none" && k.StopDown == "none");
        options.Keys.Should().NotContain(k => k.Key == "Enter" || k.Key == " ");
    }

    [Test]
    public void Details_KeyboardInterceptionFollowsTheSelectedNode()
    {
        var node = Node();
        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));
        var rootId = cut.Find(".spb-node-details").Id!;
        ConnectCount(rootId).Should().Be(1);

        cut.Render(parameters => parameters
            .Add(component => component.Node, (SpbNode?)null));
        cut.WaitForAssertion(() => DisconnectCount(rootId).Should().Be(1));

        cut.Render(parameters => parameters
            .Add(component => component.Node, node));
        cut.WaitForAssertion(() => ConnectCount(rootId).Should().Be(2));
    }

    [Test]
    public async Task Details_DisposeUnsubscribes()
    {
        var cut = RenderDetail(Node());
        var rootId = cut.Find(".spb-node-details").Id!;

        await cut.InvokeAsync(async () => await cut.Instance.DisposeAsync());

        cut.WaitForAssertion(() => DisconnectCount(rootId).Should().Be(1));
    }

    private IRenderedComponent<SparkplugNodeDetail> RenderDetail(SpbNode node) =>
        Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));

    private static SpbNode Node() => new()
    {
        NodeId = "edge-01",
        GroupId = "factory",
        Status = SpbNodeStatus.Online
    };

    private int ConnectCount(string rootId) => JSInterop.Invocations.Count(invocation =>
        invocation.Identifier == "mudKeyInterceptor.connect"
        && invocation.Arguments.Count > 1 && Equals(invocation.Arguments[1], rootId));

    private int DisconnectCount(string rootId) => JSInterop.Invocations.Count(invocation =>
        invocation.Identifier == "mudKeyInterceptor.disconnect"
        && invocation.Arguments.Count > 0 && Equals(invocation.Arguments[0], rootId));
}
