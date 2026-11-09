using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

internal sealed partial class TopicTreeStore
{
    private ImmutableArray<MqttSparkplugMetricTarget> _selectionMetrics =
        ImmutableArray<MqttSparkplugMetricTarget>.Empty;

    private int _metricMessageCount;
    private long _metricCountGeneration = -1;
    private long _metricCountContentVersion = -1;

    private bool TryApplyMetricSelection(TopicSelection selection)
    {
        if (selection.Mode != TopicSelectionMode.SparkplugMetric)
            return false;

        if (selection.Metrics.IsDefaultOrEmpty)
        {
            InvalidateSelectionIfAny();
            return true;
        }

        var metrics = TopicSelection.CanonicalizeMetrics(selection.Metrics);
        if (_selectionMode == TopicSelectionMode.SparkplugMetric
            && string.Equals(_selectionKey, selection.Key, StringComparison.Ordinal)
            && _selectionMetrics.SequenceEqual(metrics))
            return true;

        ReplaceMetricSelection(metrics, selection.Key, selection.Label);
        return true;
    }

    private SelectedTopicState CaptureSelectedTopicState(int? modeMessageCount = null)
    {
        var messageCount = _selectionMode switch
        {
            TopicSelectionMode.MultipleTopics => CountExactMessages(),
            TopicSelectionMode.Branches => modeMessageCount ?? CountBranchMessages(),
            TopicSelectionMode.SparkplugMetric => modeMessageCount ?? CountMetricMessages(),
            _ => _selectedRoots.Sum(store => store.MessageCount)
        };

        return new SelectedTopicState(
            new SelectedTopicToken(_storeId, _selectionGeneration, _selectionContentVersion),
            _selectionKey,
            messageCount,
            _selectionLabel)
        {
            Paths = _selectionPaths,
            Mode = _selectionMode,
            Branches = _selectionBranches,
            Metrics = _selectionMetrics
        };
    }

    private void ReplaceMetricSelection(
        ImmutableArray<MqttSparkplugMetricTarget> metrics, string key, string? label)
    {
        _selectedRoots.Clear();
        _selectedAncestors.Clear();
        _selectionPaths = ImmutableArray<string>.Empty;
        _selectionBranches = ImmutableArray<TopicBranchTarget>.Empty;
        _selectionMetrics = metrics;
        _selectionKey = key;
        _selectionLabel = label;
        _selectionScope = null;
        _selectionMode = TopicSelectionMode.SparkplugMetric;
        _selectionRequiresCanonical = false;
        _selectionGeneration++;
        _selectionContentVersion++;
    }

    private void CollectMetricMessages(List<MqttMessage> result)
    {
        foreach (var (store, message) in EnumerateMessages(_messageStores.Values))
        {
            if (MatchesMetricSelection(store, message))
                result.Add(message);
        }
    }

    private int CountMetricMessages()
    {
        if (_metricCountGeneration == _selectionGeneration
            && _metricCountContentVersion == _selectionContentVersion)
            return _metricMessageCount;

        _metricMessageCount = ScanMetricMessages();
        _metricCountGeneration = _selectionGeneration;
        _metricCountContentVersion = _selectionContentVersion;
        return _metricMessageCount;
    }

    private int ScanMetricMessages()
    {
        var count = 0;
        foreach (var (store, message) in EnumerateMessages(_messageStores.Values))
        {
            if (MatchesMetricSelection(store, message))
                count++;
        }

        return count;
    }

    // A message is selected once even when several selected metrics resolve from its payload.
    private bool MatchesMetricSelection(MessageStore store, MqttMessage message)
    {
        if (_selectionMetrics.IsDefaultOrEmpty)
            return false;

        var topic = message.Topic ?? store.FullTopic;
        if (message.SparkplugMetricNames.IsDefaultOrEmpty
            || !TopicBranchMatcher.TryParseSparkplug(topic, out var path))
            return false;

        return _selectionMetrics.Any(target =>
            MatchesSparkplugMetric(target, path, message.SparkplugMetricNames));
    }

    private static bool MatchesSparkplugMetric(
        MqttSparkplugMetricTarget target, SparkplugTopicPath path, ImmutableArray<string> metricNames)
    {
        if (!string.Equals(path.GroupPath, target.Scope.GroupPath, StringComparison.Ordinal)
            || !string.Equals(path.NodeId, target.Scope.NodeId, StringComparison.Ordinal))
            return false;

        var deviceMatches = target.Scope.Kind == SparkplugSelectionKind.Node
            ? path.DeviceId is null
            : target.Scope.DeviceId is not null
              && string.Equals(path.DeviceId, target.Scope.DeviceId, StringComparison.Ordinal);

        return deviceMatches
               && metricNames.Any(name => string.Equals(name, target.MetricName, StringComparison.Ordinal));
    }
}
