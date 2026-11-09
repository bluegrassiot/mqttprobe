using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public sealed class TopicBulkSelectionMetricTests
{
    private static readonly SparkplugSelectionScope _nodeScope =
        new(SparkplugSelectionKind.Node, "spBv1.0/g", "n1");

    [Test]
    public void PassiveAdoptionOfAMetric_HasNoTargetsAndLeavesCoreAlone()
    {
        var adapter = CreateAdapter();
        adapter.Add("spBv1.0/g/NDATA/n1", Message("data", 1, "Motor/Temp"));
        adapter.Store.SelectTopics(TopicSelection.ForSparkplugMetric(_nodeScope, "Motor/Temp"));
        var bulk = new TopicBulkSelection(adapter);
        bulk.SetMultiSelect(false);

        bulk.AdoptSingle(adapter.Store.GetSelectedTopicState()).Should().BeFalse();

        bulk.Count.Should().Be(0, "a metric key is not a browsable branch");
        bulk.HasSelection.Should().BeFalse();
        adapter.Selections.Should().BeEmpty("adoption must not write a fake branch back to Core");
        adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SparkplugMetric);
        adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("Motor/Temp");
    }

    [Test]
    public void PassiveAdoption_DropsPreviouslyCheckedTargetsWithoutClearingTheMetric()
    {
        var adapter = CreateAdapter();
        adapter.Add("spBv1.0/g/NDATA/n1", Message("data", 1, "Motor/Temp"));
        var bulk = new TopicBulkSelection(adapter);
        bulk.SetMultiSelect(true);
        bulk.SelectAll([new TopicBranchTarget(TopicBranchKind.Raw, Path: "a")]);
        adapter.Store.SelectTopics(TopicSelection.ForSparkplugMetric(_nodeScope, "Motor/Temp"));
        bulk.SetMultiSelect(false);

        bulk.AdoptSingle(adapter.Store.GetSelectedTopicState());

        bulk.Count.Should().Be(0);
        adapter.Store.GetSelectedTopicState().Mode.Should().Be(TopicSelectionMode.SparkplugMetric,
            "clearing the checked set must never clear Core's metric filter");
    }

    [Test]
    public void PassiveBulkActions_DoNotClearTheMetric()
    {
        var adapter = CreateAdapter();
        adapter.Add("spBv1.0/g/NDATA/n1", Message("data", 1, "Motor/Temp"));
        adapter.Store.SelectTopics(TopicSelection.ForSparkplugMetric(_nodeScope, "Motor/Temp"));
        var bulk = new TopicBulkSelection(adapter);
        bulk.SetMultiSelect(false);
        adapter.Selections.Clear();

        bulk.Clear();
        bulk.SelectAll([new TopicBranchTarget(TopicBranchKind.Raw, Path: "a")]);
        bulk.Toggle(new TopicBranchTarget(TopicBranchKind.Raw, Path: "a"));
        bulk.Reconcile(_ => false);

        adapter.Selections.Should().BeEmpty();
        adapter.Store.GetSelectedTopicState().Metric!.MetricName.Should().Be("Motor/Temp");
    }

    [Test]
    public void CaptureBrowsedTopicForClear_IgnoresAMetricSelection()
    {
        var adapter = CreateAdapter();
        adapter.Add("spBv1.0/g/NDATA/n1", Message("data", 1, "Motor/Temp"));
        adapter.Store.SelectTopics(TopicSelection.ForSparkplugMetric(_nodeScope, "Motor/Temp"));
        var bulk = new TopicBulkSelection(adapter);
        bulk.SetMultiSelect(false);

        bulk.CaptureBrowsedTopicForClear().Should().BeNull();
    }

    private static MqttMessage Message(string payload, int second, params string[] metricNames) =>
        new(payload, "unused", false, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        {
            Topic = "spBv1.0/g/NDATA/n1",
            DateTimeReceived = DateTime.UnixEpoch.AddSeconds(second),
            SparkplugMetricNames = [.. metricNames]
        };

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
}
