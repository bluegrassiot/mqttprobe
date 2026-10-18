using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugSequenceDiagnosticsTests : BunitTestContext
{
    [SetUp]
    public void Setup()
    {
        EnsureMudProviders();
    }

    [TestCase(SpbSequenceState.Unknown, "Not enough data")]
    [TestCase(SpbSequenceState.InOrder, "In order")]
    [TestCase(SpbSequenceState.Reordering, "Waiting for late messages")]
    [TestCase(SpbSequenceState.GapDetected, "Unresolved gap")]
    [TestCase(SpbSequenceState.Invalid, "Invalid sequence")]
    public void Detail_ShowsSequenceState(SpbSequenceState state, string expectedLabel)
    {
        var node = CreateNode(state);

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));

        cut.Find(".spb-sequence-state").TextContent.Should().Be(expectedLabel);
    }

    [Test]
    public void Detail_ShowsZeroSequenceValuesAndDistinguishesBdSeq()
    {
        var node = CreateNode(SpbSequenceState.InOrder);
        node.Sequence = new SpbSequenceDiagnostics
        {
            State = SpbSequenceState.InOrder,
            ExpectedSequence = 0,
            ReceivedSequence = 0
        };
        node.BirthDeathSequenceNumber = 0;

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));

        cut.Markup.Should().Contain("Next expected");
        cut.Markup.Should().Contain("Last received");
        cut.FindAll(".spb-sequence-values").Single().TextContent.Should().Contain("Next expected 0").And.Contain("Last received 0");
        cut.Markup.Should().Contain("Birth/death session identifier (bdSeq):");
        cut.Markup.Should().Contain("<span class=\"metrics-mono\">0</span>");

        node.BirthDeathSequenceNumber = null;
        var unknown = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));
        unknown.Markup.Should().Contain("Birth/death session identifier (bdSeq):");
        unknown.Markup.Should().Contain("<span class=\"metrics-mono\">Unknown</span>");
    }

    [TestCase(new byte[] { 10 }, "10")]
    [TestCase(new byte[] { 10, 11 }, "10-11")]
    [TestCase(new byte[] { 10, 11, 12, 13, 14, 15, 18, 20, 21, 22 }, "10-15, 18, 20-22")]
    [TestCase(new byte[] { 12, 10, 11 }, "12, 10-11")]
    [TestCase(new byte[] { 254, 255, 0, 1, 2 }, "254-255, 0-2")]
    public void Detail_RendersMissingSequencesAsOrderedRanges(byte[] missingSequences, string expected)
    {
        var node = CreateNode(SpbSequenceState.GapDetected);
        node.Sequence = new SpbSequenceDiagnostics
        {
            State = SpbSequenceState.GapDetected,
            MissingSequences = missingSequences
        };

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));

        cut.Find(".spb-sequence-values").TextContent.Should().Contain($"Missing sequences {expected}");
    }

    [Test]
    public void Detail_DoesNotRenderMissingSequencesWhenListIsEmpty()
    {
        var node = CreateNode(SpbSequenceState.InOrder);

        var cut = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));

        cut.Find(".spb-sequence-values").TextContent.Should().NotContain("Missing sequences");
    }

    [Test]
    public void Detail_ExplainsPendingAndExpiredGaps()
    {
        var pending = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, CreateNode(SpbSequenceState.Reordering)));
        pending.Markup.Should().Contain("Waiting for late messages");

        var expired = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, CreateNode(SpbSequenceState.GapDetected)));
        expired.Markup.Should().Contain("Unresolved gaps in observed messages");
        expired.Markup.Should().Contain("Filtered, excluded or rate-limited traffic can also appear as gaps");
    }

    [Test]
    public void NodeList_ShowsCompactWarningSeparateFromNodeStatus()
    {
        var node = CreateNode(SpbSequenceState.GapDetected);
        node.Sequence = new SpbSequenceDiagnostics
        {
            State = SpbSequenceState.GapDetected,
            ExpectedSequence = 4,
            ReceivedSequence = 6,
            MissingSequences = [2, 3]
        };
        var group = new SpbGroup { GroupId = "factory" };
        group.Nodes[node.NodeId] = node;

        var cut = Render<SparkplugNodeList>(parameters => parameters
            .Add(component => component.Groups, new[] { group.Nodes.Values.GroupBy(item => item.GroupId).Single() }));

        cut.FindAll(".spb-status-label--node").Single().TextContent.Should().Contain("ONLINE");
        cut.Find(".spb-sequence-warning").TextContent.Should().Contain("2 missing");
        var detail = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));
        detail.Find(".spb-sequence-values").TextContent.Should().Contain("Next expected 4").And.Contain("Last received 6").And.Contain("Missing sequences 2-3");
    }

    [Test]
    public void StaleNode_KeepsWarningColorAndLabelInListAndDetail()
    {
        var node = CreateNode(SpbSequenceState.Unknown);
        node.Status = SpbNodeStatus.Offline;
        node.LastDataAt = DateTime.UtcNow;
        var group = new SpbGroup { GroupId = node.GroupId };
        group.Nodes[node.NodeId] = node;

        var list = Render<SparkplugNodeList>(parameters => parameters
            .Add(component => component.Groups, [group.Nodes.Values.GroupBy(item => item.GroupId).Single()]));
        var detail = Render<SparkplugNodeDetail>(parameters => parameters
            .Add(component => component.Node, node));

        list.Find(".spb-status-label--node").TextContent.Should().Be("STALE");
        list.Find(".spb-status-label--node").ClassList.Should().Contain("spb-status--stale");
        detail.Find(".spb-status-label").TextContent.Should().Be("STALE");
        detail.Find(".spb-status-label").ClassList.Should().Contain("spb-status--stale");
    }

    private static SpbNode CreateNode(SpbSequenceState state) => new()
    {
        NodeId = "edge-01",
        GroupId = "factory",
        Status = SpbNodeStatus.Online,
        Sequence = new SpbSequenceDiagnostics { State = state }
    };
}
