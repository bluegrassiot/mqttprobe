using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicSelectionSessionMetricTests
{
    private static readonly SparkplugSelectionScope _nodeScope =
        new(SparkplugSelectionKind.Node, "spBv1.0/g", "n1");

    private const string NodeTopic = "spBv1.0/g/NDATA/n1";

    [Test]
    public void ValidMetricSelection_SurvivesCollapseSearchAndZeroMatches()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("alias-only", 1, "Motor/Temp"));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);
        session.Refresh(null, EmptyExpanded(), grouped: true, metricSource: Metrics("Motor/Temp"))
            .Should().NotBeNull();

        var filtered = session.Refresh("no-match", EmptyExpanded(), grouped: true, force: true,
            metricSource: Metrics("Motor/Temp"));

        filtered.Should().NotBeNull();
        filtered.Selection.Metric.Should().Be(new MqttSparkplugMetricTarget(_nodeScope, "Motor/Temp"));
        filtered.Selection.Key.Should().NotBeNull();
        adapter.ConditionalClearCalls.Should().Be(0, "a collapsed or filtered metric selection stays live");

        adapter.Add(NodeTopic, Message("without-the-metric", 2));
        var noMatches = session.Refresh(null, EmptyExpanded(), grouped: true, force: true,
            metricSource: Metrics("Motor/Temp"));

        noMatches.Should().NotBeNull();
        noMatches.Selection.Metric.Should().NotBeNull();
        noMatches.Selection.MessageCount.Should().Be(1,
            "zero new matches do not drop the metric filter");
    }

    [Test]
    public void GroupingOff_ClearsTheMetricSelectionConditionally()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);

        var frame = session.Refresh(null, EmptyExpanded(), grouped: false, metricSource: Metrics("Motor/Temp"));

        frame.Should().NotBeNull();
        frame.Selection.Key.Should().BeNull();
        frame.Selection.Metrics.Should().BeEmpty();
        adapter.ConditionalReplaceCalls.Should().Be(1,
            "the replacement is a compare-and-swap, not a blind write");
        adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public void EnteringMultiSelect_KeepsAValidMetricAndItsRows()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);

        var frame = session.Refresh(null, ExpandedWithMetrics(adapter, Metrics("Motor/Temp")),
            grouped: true, selectionMode: true, metricSource: Metrics("Motor/Temp"));

        frame.Should().NotBeNull();
        frame.Selection.Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        frame.Selection.Metric.Should().Be(new MqttSparkplugMetricTarget(_nodeScope, "Motor/Temp"));
        frame.Rows.Should().Contain(row => row.IsMetricRow && row.MetricName == "Motor/Temp");
        frame.Rows.Should().Contain(row => row.IsMetricGroup);
        adapter.ConditionalReplaceCalls.Should().Be(0,
            "turning the preference on is not a reason to end a valid metric filter");
    }

    [Test]
    public void EnteringMultiSelect_StillClearsAMetricTheTopologyLost()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);

        var frame = session.Refresh(null, EmptyExpanded(), grouped: true, selectionMode: true,
            metricSource: Metrics());

        frame.Should().NotBeNull();
        frame.Selection.Key.Should().BeNull();
        adapter.ConditionalReplaceCalls.Should().Be(1);
    }

    [Test]
    public void OwnerPurge_ClearsTheMetricSelection()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        adapter.Add("other/topic", Message("other", 2));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);

        adapter.Store.RemoveMatchingTopic(NodeTopic);
        var frame = session.Refresh(null, EmptyExpanded(), grouped: true, metricSource: Metrics("Motor/Temp"));

        frame.Should().NotBeNull();
        frame.Selection.Key.Should().BeNull();
        adapter.Store.GetSelectedTopicState().Key.Should().BeNull();
    }

    [Test]
    public void BirthReplacementWithoutTheMetric_ClearsTheMetricSelection()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);
        session.Refresh(null, EmptyExpanded(), grouped: true, metricSource: Metrics("Motor/Temp"));

        var frame = session.Refresh(null, EmptyExpanded(), grouped: true, force: true,
            metricSource: Metrics());

        frame.Should().NotBeNull();
        frame.Selection.Key.Should().BeNull("a birth that no longer lists the metric ends the filter");
    }

    [Test]
    public void TopologyChange_ForcesAFrameEvenWhenTheTreeVersionDoesNotMove()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);
        session.Refresh(null, EmptyExpanded(), grouped: true, metricSource: Metrics("Motor/Temp"));

        session.Refresh(null, EmptyExpanded(), grouped: true, metricSource: Metrics("Motor/Temp"))
            .Should().BeNull("an unchanged tree and selection hit the cursor");

        var forced = session.Refresh(null, EmptyExpanded(), grouped: true, force: true,
            metricSource: Metrics("Motor/Temp"));

        forced.Should().NotBeNull("TopologyChanged forces one refresh without a tree version change");
        forced.Selection.Metric.Should().NotBeNull();
    }

    [Test]
    public void RejectedConditionalReplace_LeavesTheFrameAndCursorUntouched()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        SelectMetric(adapter, "Motor/Temp");
        var session = new TopicSelectionSession(adapter);
        var original = session.Refresh(null, EmptyExpanded(), grouped: true,
            metricSource: Metrics("Motor/Temp"))!;

        adapter.ReturnSnapshotOnce(new TopicTreeSnapshot(adapter.Store.Version,
            ImmutableArray<TopicNodeSnapshot>.Empty));
        adapter.BeforeReplace = () => adapter.Store.SelectTopics(
            TopicSelection.ForSparkplugMetric(_nodeScope, "Another"));

        session.Refresh(null, EmptyExpanded(), grouped: true, force: true, metricSource: Metrics())
            .Should().BeNull();
        session.Frame.Should().BeSameAs(original);

        adapter.BeforeReplace = null;
        var recovered = session.Refresh(null, EmptyExpanded(), grouped: true, force: true,
            metricSource: Metrics("Motor/Temp", "Another"));

        recovered.Should().NotBeNull();
        recovered.Selection.Metric!.MetricName.Should().Be("Another",
            "the newer selection is the one the store kept");
    }

    [Test]
    public void PruningKeepsTheSurvivingMetricsAcrossOwners()
    {
        var adapter = CreateAdapter();
        adapter.Add(NodeTopic, Message("data", 1, "Motor/Temp"));
        adapter.Add("spBv1.0/g/NDATA/n2", Message("other", 2, "Speed"));
        var n2Scope = new SparkplugSelectionScope(SparkplugSelectionKind.Node, "spBv1.0/g", "n2");
        var first = new MqttSparkplugMetricTarget(_nodeScope, "Motor/Temp");
        var second = new MqttSparkplugMetricTarget(n2Scope, "Speed");
        adapter.Store.SelectTopics(TopicSelection.ForSparkplugMetrics([first, second]));
        var session = new TopicSelectionSession(adapter);

        var frame = session.Refresh(null, EmptyExpanded(), grouped: true,
            metricSource: scope => scope.Equals(_nodeScope)
                ? [new SpbMetricSnapshot("Motor/Temp", "int32", "1", DateTime.UnixEpoch)]
                : []);

        frame.Should().NotBeNull();
        frame.Selection.Metrics.Should().Equal(first);
        adapter.ConditionalReplaceCalls.Should().Be(1);
        adapter.Store.GetSelectedTopicState().Metrics.Should().Equal(first);
    }

    private static IReadOnlySet<string> EmptyExpanded() => new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> ExpandedWithMetrics(
        TopicTreeSelectionTestAdapter adapter,
        Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>> source)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        TopicTreeProjection.AddExpandablePaths(
            adapter.Store.GetTopicTreeSnapshot().Roots, groupSparkplugTopics: true, ids,
            new MetricProjection(source));
        return ids;
    }

    private static Func<SparkplugSelectionScope, IReadOnlyList<SpbMetricSnapshot>> Metrics(
        params string[] names) =>
        _ => [.. names.Select(name => new SpbMetricSnapshot(name, "int32", "1", DateTime.UnixEpoch))];

    private static void SelectMetric(TopicTreeSelectionTestAdapter adapter, string name) =>
        adapter.Store.SelectTopics(TopicSelection.ForSparkplugMetric(_nodeScope, name));

    private static TopicTreeSelectionTestAdapter CreateAdapter()
    {
        var configuration = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 100, MaxTopicNodes = 100 }
        };
        var settings = Substitute.For<IPerformanceSettings>();
        settings.Performance.Returns(configuration.Performance);
        return new TopicTreeSelectionTestAdapter(new TopicTreeStore(settings, Substitute.For<ILogger>()));
    }

    private static MqttMessage Message(string payload, int second, params string[] metricNames) =>
        new(payload, "unused", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = NodeTopic,
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second),
            SparkplugMetricNames = [.. metricNames]
        };
}
