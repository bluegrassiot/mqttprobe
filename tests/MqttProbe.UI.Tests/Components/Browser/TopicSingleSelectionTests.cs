using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicSingleSelectionTests
{
    private TopicBrowserTestFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new TopicBrowserTestFixture();
        _fixture.AddTopics(("sensors/temp", 2), ("sensors/temp/room1", 1));
    }

    [Test]
    public void SingleTopic_MapsToItsBranchTarget_AndLeavesTheStoreSelectionAlone()
    {
        _fixture.SetSelection(TopicSelection.ForTopic("sensors/temp"));
        var stored = _fixture.Adapter.Store.GetSelectedTopicState();
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SetMultiSelect(false);

        selection.AdoptSingle(stored).Should().BeTrue();

        selection.Branches.Should().ContainSingle();
        selection.Branches[0].Kind.Should().Be(TopicBranchKind.Raw);
        selection.Branches[0].Path.Should().Be("sensors/temp");
        selection.Summary.Should().Be("Selected topic and its subtopics: \"sensors/temp\"");
        selection.CompactSummary.Should().Be("Topic and subtopics: \"sensors/temp\"");

        var current = _fixture.Adapter.Store.GetSelectedTopicState();
        current.Mode.Should().Be(TopicSelectionMode.SingleTopic);
        current.Key.Should().Be("sensors/temp");
        current.Paths.Should().Equal("sensors/temp");
    }

    [TestCase(SparkplugSelectionKind.Node, "Selected node: spBv1.0/plant1/node1", "Node: spBv1.0/plant1/node1")]
    [TestCase(SparkplugSelectionKind.Devices, "Selected devices: spBv1.0/plant1/node1", "Devices: spBv1.0/plant1/node1")]
    [TestCase(SparkplugSelectionKind.Device, "Selected device: spBv1.0/plant1/node1/dev1",
        "Device: spBv1.0/plant1/node1/dev1")]
    public void GroupedScope_MapsToItsBranchAndNamesTheScope(
        SparkplugSelectionKind kind, string summary, string compact)
    {
        var branch = SparkplugBranch(kind);
        _fixture.SetSelection(TopicSelection.ForBranches([branch]));
        var stored = _fixture.Adapter.Store.GetSelectedTopicState();
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SetMultiSelect(false);

        selection.AdoptSingle(stored);

        selection.Branches.Should().ContainSingle().Which.Should().Be(branch);
        selection.Summary.Should().Be(summary);
        selection.CompactSummary.Should().Be(compact);
        _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.Branches);
    }

    [Test]
    public void TurningMultiSelectOff_KeepsASingleBranchSelected()
    {
        var branch = SparkplugBranch(SparkplugSelectionKind.Node);
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([branch]);

        selection.SetMultiSelect(false);

        selection.IsPassive.Should().BeTrue();
        selection.HasSelection.Should().BeTrue();
        selection.Branches.Should().ContainSingle().Which.Should().Be(branch);
        _fixture.Adapter.Store.GetSelectedTopicState().Branches.Should().ContainSingle().Which.Should().Be(branch);
    }

    [Test]
    public void TurningMultiSelectOff_ClearsSeveralBranches()
    {
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SelectAll([
            new TopicBranchTarget(TopicBranchKind.Raw, Path: "sensors/temp"),
            new TopicBranchTarget(TopicBranchKind.Raw, Path: "sensors/temp/room1")
        ]);

        selection.SetMultiSelect(false);

        selection.IsPassive.Should().BeTrue();
        selection.HasSelection.Should().BeFalse();
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public void PassiveSelection_IgnoresBulkToggles()
    {
        _fixture.SetSelection(TopicSelection.ForTopic("sensors/temp"));
        var stored = _fixture.Adapter.Store.GetSelectedTopicState();
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SetMultiSelect(false);
        selection.AdoptSingle(stored);

        selection.Toggle(new TopicBranchTarget(TopicBranchKind.Raw, Path: "sensors/temp"));
        selection.SelectAll([new TopicBranchTarget(TopicBranchKind.Raw, Path: "sensors/temp/room1")]);

        selection.Branches.Should().ContainSingle().Which.Path.Should().Be("sensors/temp");
        _fixture.Adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SingleTopic);
        _fixture.Adapter.Store.GetSelectedTopicState().Key.Should().Be("sensors/temp");
    }

    [Test]
    public void TheSameSelection_IsNotReapplied()
    {
        _fixture.SetSelection(TopicSelection.ForTopic("sensors/temp"));
        var stored = _fixture.Adapter.Store.GetSelectedTopicState();
        var selection = new TopicBulkSelection(_fixture.Adapter);
        selection.SetMultiSelect(false);
        selection.AdoptSingle(stored).Should().BeTrue();

        selection.AdoptSingle(stored).Should().BeFalse();
    }

    private static TopicBranchTarget SparkplugBranch(SparkplugSelectionKind kind) =>
        new(TopicBranchKind.Sparkplug,
            Scope: new SparkplugSelectionScope(kind, "spBv1.0/plant1", "node1",
                kind == SparkplugSelectionKind.Device ? "dev1" : null));
}
