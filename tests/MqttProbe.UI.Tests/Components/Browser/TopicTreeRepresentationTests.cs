using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

// The answer must ignore expansion and filtering: a topic hidden only because its branch is
// collapsed or filtered out is still represented.
[TestFixture]
public sealed class TopicTreeRepresentationTests
{
    private static ImmutableArray<TopicNodeSnapshot> Tree() => TopicSnapshotBuilder.Build(
        ("spBv1.0/plant1/NDATA/node1", 3),
        ("spBv1.0/plant1/NDATA/node1/extra", 1),
        ("spBv1.0/plant1/NDATA/node2", 1),
        ("spBv1.0/plant1/NBIRTH/node1", 1),
        ("spBv1.0/plant1/DDATA/node1/dev1", 2),
        ("spBv1.0/plant1/STATE/scada", 1),
        ("spBv1.0/STATE/scada-host", 1),
        ("sensors/temp", 2));

    [Test]
    public void GroupingOff_RepresentsEveryStorePath()
    {
        var tree = Tree();

        foreach (var path in new[]
                 {
                     "spBv1.0", "spBv1.0/plant1", "spBv1.0/plant1/NDATA", "spBv1.0/plant1/NDATA/node1",
                     "spBv1.0/plant1/DDATA/node1", "spBv1.0/plant1/STATE/scada", "sensors", "sensors/temp"
                 })
        {
            TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: false, path)
                .Should().BeTrue(path);
        }
    }

    [Test]
    public void GroupingOff_RejectsPathsTheStoreDoesNotHave()
    {
        var tree = Tree();

        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: false, "sensors/humidity")
            .Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: false, null).Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(default, groupSparkplugTopics: false, "sensors")
            .Should().BeFalse();
    }

    [Test]
    public void GroupingOn_RepresentsNamespaceGroupTipsAndLiteralFallbacks()
    {
        var tree = Tree();

        foreach (var path in new[]
                 {
                     "spBv1.0", "spBv1.0/plant1", "spBv1.0/STATE",
                     "spBv1.0/plant1/NDATA/node1", "spBv1.0/plant1/NBIRTH/node1",
                     "spBv1.0/plant1/NDATA/node2", "spBv1.0/plant1/DDATA/node1/dev1",
                     "spBv1.0/plant1/NDATA/node1/extra", "spBv1.0/plant1/STATE/scada",
                     "spBv1.0/STATE/scada-host", "sensors", "sensors/temp"
                 })
        {
            TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, path)
                .Should().BeTrue(path);
        }
    }

    [Test]
    public void GroupingOn_RejectsRegroupedIntermediatesAndAbsentPaths()
    {
        var tree = Tree();

        foreach (var path in new[]
                 {
                     "spBv1.0/plant1/NDATA", "spBv1.0/plant1/NBIRTH",
                     "spBv1.0/plant1/DDATA", "spBv1.0/plant1/DDATA/node1"
                 })
        {
            TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, path)
                .Should().BeFalse(path);
        }

        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, "spBv1.0/plant1/NCMD/node1")
            .Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(tree, groupSparkplugTopics: true, null).Should().BeFalse();
        TopicTreeProjection.RepresentsRealPath(default, groupSparkplugTopics: true, "spBv1.0")
            .Should().BeFalse();
    }
}
