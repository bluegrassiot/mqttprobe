using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class TopicExcludeService
{
    // Purgers always run with _sync held; only a purge running inline with Add or Remove
    // also holds the operation semaphore, never a purge finished later (reader release,
    // connection load). They may mutate state but must not notify observers: a subscriber
    // re-entering Add from here deadlocks against the lock or the semaphore still held.
    private void RunPurgersLocked(PendingPurge purge)
    {
        if (purge.Rule is { } rule)
        {
            RunSparkplugPurgersLocked(purge, rule);
            return;
        }

        if (purge.Filter is not { } filter || PurgeExcludedTopic is not { } purgers)
            return;

        foreach (var purger in purgers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                purger(filter);
            }
            catch (Exception ex)
            {
                purge.Failed = true;
                _logger.LogError(ex, "Failed to purge excluded topic data for {Filter}", filter);
            }
        }
    }

    private void RunSparkplugPurgersLocked(PendingPurge purge, SparkplugTopicExclude rule)
    {
        if (PurgeExcludedSparkplug is not { } purgers)
            return;

        foreach (var purger in purgers.GetInvocationList().Cast<Action<SparkplugTopicExclude>>())
        {
            try
            {
                purger(rule);
            }
            catch (Exception ex)
            {
                purge.Failed = true;
                _logger.LogError(ex, "Failed to purge excluded Sparkplug branch data for {Scope}", rule.Scope);
            }
        }
    }

    private PurgeOutcome FinishPurge(PendingPurge purge)
    {
        var outcome = purge.Failed ? PurgeOutcome.Failed : PurgeOutcome.Completed;
        try
        {
            if (purge.Rule is { } rule)
                NotifySparkplugExcluded(rule);
            else if (purge.Filter is { } filter)
                NotifyTopicExcluded(filter);
        }
        finally
        {
            purge.Completion.TrySetResult(outcome);
        }

        return outcome;
    }

    private void NotifySparkplugExcluded(SparkplugTopicExclude rule)
    {
        if (SparkplugExcluded is not { } observers)
            return;

        foreach (var observer in observers.GetInvocationList().Cast<Action<SparkplugTopicExclude>>())
        {
            try
            {
                observer(rule);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sparkplug exclusion observer failed for {Scope}", rule.Scope);
            }
        }
    }

    private void NotifyTopicExcluded(string filter)
    {
        if (TopicExcluded is not { } observers)
            return;

        foreach (var observer in observers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                observer(filter);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Topic exclusion observer failed for {Filter}", filter);
            }
        }
    }

    private bool HasMatchingReaderLocked(PendingPurge purge) =>
        purge.Rule is { } rule
            ? _inFlightReaders.Keys.Any(topic =>
                TopicBranchMatcher.MatchesSparkplug(rule.Scope, rule.MessageType, topic))
            : purge.Filter is { } filter && _inFlightReaders.Keys.Any(topic => Matches(topic, filter));

    private bool IsActiveLocked(PendingPurge purge) =>
        purge.Rule is { } rule
            ? _sparkplugRules.Contains(rule)
            : purge.Filter is { } filter && _topics.Contains(filter);

    private void DropPendingPurgesLocked()
    {
        foreach (var purge in _pendingPurges)
            purge.Completion.TrySetResult(PurgeOutcome.Superseded);

        _pendingPurges.Clear();
    }

    // A parked purge whose target is gone no longer has anything to clear, so it settles
    // here with the outcome the caller can act on instead of waiting for a reader release.
    private void DropRemovedPendingPurgesLocked()
    {
        for (var i = _pendingPurges.Count - 1; i >= 0; i--)
        {
            var purge = _pendingPurges[i];
            if (IsActiveLocked(purge))
                continue;

            _pendingPurges.RemoveAt(i);
            purge.Completion.TrySetResult(PurgeOutcome.Removed);
        }
    }

    // Parked purges publish here: the write they were waiting for has now happened.
    private void ReleaseRead(string topic)
    {
        List<PendingPurge>? purges = null;
        lock (_sync)
        {
            if (_inFlightReaders.TryGetValue(topic, out var readers))
            {
                if (readers > 1)
                    _inFlightReaders[topic] = readers - 1;
                else
                    _inFlightReaders.Remove(topic);
            }

            for (var i = _pendingPurges.Count - 1; i >= 0; i--)
            {
                var purge = _pendingPurges[i];
                if (purge.Generation != _connectionGeneration || !IsActiveLocked(purge))
                {
                    _pendingPurges.RemoveAt(i);
                    purge.Completion.TrySetResult(PurgeOutcome.Superseded);
                }
                else if (!HasMatchingReaderLocked(purge))
                {
                    _pendingPurges.RemoveAt(i);
                    RunPurgersLocked(purge);
                    (purges ??= []).Add(purge);
                }
            }
        }

        if (purges is null)
            return;

        foreach (var purge in purges)
            FinishPurge(purge);
    }

    private enum PurgeOutcome
    {
        Superseded,
        Completed,
        Failed,

        // The filter was removed again before its purge could run.
        Removed
    }

    private sealed class PendingPurge(string? filter, long generation, SparkplugTopicExclude? rule)
    {
        public string? Filter { get; } = filter;
        public long Generation { get; } = generation;
        public SparkplugTopicExclude? Rule { get; } = rule;
        public bool Failed { get; set; }
        public TaskCompletionSource<PurgeOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static PendingPurge ForFilter(string filter, long generation) =>
            new(filter, generation, null);

        public static PendingPurge ForSparkplug(SparkplugTopicExclude rule, long generation) =>
            new(null, generation, rule);
    }
}
