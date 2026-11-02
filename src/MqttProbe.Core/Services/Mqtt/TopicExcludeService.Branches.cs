using System.Collections.Immutable;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Plugins.Protobuf;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class TopicExcludeService
{
    public TopicExcludeConnectionToken CaptureConnectionToken()
    {
        lock (_sync)
        {
            var (connection, revision) = _sessionState.CaptureSelectedConnection();
            return new(_connectionGeneration, connection.Id, revision);
        }
    }

    public bool TryExecuteForConnection<T>(
        TopicExcludeConnectionToken token, Func<T> mutation, out T result)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_sync)
        {
            if (_disposed || token.Generation != _connectionGeneration)
            {
                result = default!;
                return false;
            }

            var (selected, revision) = _sessionState.CaptureSelectedConnection();
            if (_loadedConnection is null
                || !ReferenceEquals(_loadedConnection, selected)
                || selected.Id != token.ConnectionId
                || revision != token.SelectionRevision)
            {
                result = default!;
                return false;
            }

            return _sessionState.TryExecuteForSelectedConnection(
                token.ConnectionId, token.SelectionRevision, mutation, out result);
        }
    }

    public async Task<TopicBranchExcludeBatchResult> AddBranches(
        IReadOnlyList<TopicBranchTarget> targets, TopicExcludeConnectionToken token)
    {
        var results = new List<TopicBranchExcludeResult>(targets.Count);
        foreach (var target in targets)
        {
            var (outcome, feedback) = await AddBranchCore(target, token);
            results.Add(new(target, outcome, feedback));
        }

        return new TopicBranchExcludeBatchResult(ImmutableArray.CreateRange(results));
    }

    public async Task<TopicExcludeOperationResult> RemoveBranches(IReadOnlyList<TopicBranchTarget> targets)
    {
        var (filters, rules) = CollectBranchRemovals(targets);

        Connection activeConnection;
        long generation;
        long selectionRevision;
        lock (_sync)
        {
            (activeConnection, selectionRevision) = _sessionState.CaptureSelectedConnection();
            generation = _connectionGeneration;
        }

        if (!await EnterOperationAsync())
            return ServiceClosedFailure();
        try
        {
            string[] candidate;
            SparkplugTopicExclude[] candidateRules;
            lock (_sync)
            {
                if (!IsSelectionCurrentLocked(activeConnection, selectionRevision, generation))
                    return ConnectionChangedFailure();

                candidate = [.. _topics.Where(topic => !filters.Contains(topic))];
                candidateRules = [.. _sparkplugRules.Where(rule => !rules.Contains(rule))];
            }

            var persistenceError = await PersistTopicsAsync(activeConnection, candidate, candidateRules);
            if (persistenceError is not null)
                return PersistenceFailure();

            lock (_sync)
            {
                if (_disposed)
                    return ServiceClosedFailure();

                if (!IsSelectionCurrentLocked(activeConnection, selectionRevision, generation))
                    return ConnectionChangedFailure();

                _topics.Clear();
                foreach (var topic in candidate)
                    _topics.Add(topic);
                _sparkplugRules.Clear();
                _sparkplugRules.AddRange(candidateRules);
                DropRemovedPendingPurgesLocked();
            }

            return new(true);
        }
        finally
        {
            ReleaseOperationLock();
        }
    }

    private static (HashSet<string> Filters, HashSet<SparkplugTopicExclude> Rules) CollectBranchRemovals(
        IReadOnlyList<TopicBranchTarget> targets)
    {
        var filters = new HashSet<string>(StringComparer.Ordinal);
        var rules = new HashSet<SparkplugTopicExclude>();
        foreach (var target in targets)
        {
            if (!TryNormalizeBranch(target, out var filter, out var rule, out _))
                continue;

            if (rule is null)
                filters.Add(filter);
            else
                rules.Add(rule);
        }

        return (filters, rules);
    }

    private bool IsSelectionCurrentLocked(Connection connection, long revision, long generation)
    {
        var (selected, currentRevision) = _sessionState.CaptureSelectedConnection();
        return currentRevision == revision
               && ReferenceEquals(selected, connection)
               && ReferenceEquals(_loadedConnection, connection)
               && IsCurrentConnectionLocked(connection, generation);
    }

    private async Task<(TopicExcludeAddOutcome Outcome, UserNotification? Feedback)> AddBranchCore(
        TopicBranchTarget target, TopicExcludeConnectionToken token)
    {
        if (!TryNormalizeBranch(target, out var filter, out var rule, out var invalidFeedback))
            return (TopicExcludeAddOutcome.Invalid, invalidFeedback);

        if (!await EnterOperationAsync())
            return (TopicExcludeAddOutcome.Failed, ServiceClosedFailure().Feedback);

        PendingPurge? purge;
        var deferred = false;
        try
        {
            if (TryPrepareBranchLocked(target, token, filter, rule,
                    out var activeConnection, out var generation,
                    out var candidate, out var candidateRules) is { } failure)
                return failure;

            var persistenceError = await PersistTopicsAsync(activeConnection, candidate, candidateRules);
            if (persistenceError is not null)
                return (TopicExcludeAddOutcome.Failed, PersistenceFailure().Feedback);

            purge = ApplyBranchLocked(filter, rule, token, activeConnection, generation,
                out var failureOutcome, out var failureFeedback, out deferred);
            if (purge is null)
                return (failureOutcome, failureFeedback);
        }
        finally
        {
            ReleaseOperationLock();
        }

        return await SettlePurgeAsync(purge, deferred);
    }

    private (TopicExcludeAddOutcome Outcome, UserNotification? Feedback)? TryPrepareBranchLocked(
        TopicBranchTarget target, TopicExcludeConnectionToken token,
        string filter, SparkplugTopicExclude? rule,
        out Connection activeConnection, out long generation,
        out string[] candidate, out SparkplugTopicExclude[] candidateRules)
    {
        candidate = [];
        candidateRules = [];
        lock (_sync)
        {
            var (selected, revision) = _sessionState.CaptureSelectedConnection();
            activeConnection = selected;
            generation = _connectionGeneration;
            if (generation != token.Generation
                || activeConnection.Id != token.ConnectionId
                || revision != token.SelectionRevision
                || !ReferenceEquals(_loadedConnection, activeConnection)
                || !IsCurrentConnectionLocked(activeConnection, generation))
                return (TopicExcludeAddOutcome.ConnectionChanged, ConnectionChangedFailure().Feedback);

            if (ValidateBranchAddLocked(target, filter, rule) is { } failure)
                return failure;

            candidate = rule is null ? [.. _topics, filter] : [.. _topics];
            candidateRules = rule is null ? [.. _sparkplugRules] : [.. _sparkplugRules, rule];
            return null;
        }
    }

    private (TopicExcludeAddOutcome Outcome, UserNotification? Feedback)? ValidateBranchAddLocked(
        TopicBranchTarget target, string filter, SparkplugTopicExclude? rule)
    {
        if (rule is null)
        {
            if (_topics.Contains(filter))
                return (TopicExcludeAddOutcome.AlreadyExcluded, AlreadyExcludedFeedback(filter));
        }
        else if (_sparkplugRules.Contains(rule))
        {
            return (TopicExcludeAddOutcome.AlreadyExcluded,
                AlreadyExcludedFeedback(TopicSelection.BranchLabel(target)));
        }

        return _topics.Count + _sparkplugRules.Count >= MaxTopicExcludes
            ? (TopicExcludeAddOutcome.LimitReached, LimitReachedFeedback())
            : null;
    }

    private PendingPurge? ApplyBranchLocked(
        string filter, SparkplugTopicExclude? rule, TopicExcludeConnectionToken token,
        Connection activeConnection, long generation,
        out TopicExcludeAddOutcome failureOutcome, out UserNotification? failureFeedback, out bool deferred)
    {
        failureOutcome = TopicExcludeAddOutcome.Failed;
        failureFeedback = null;
        deferred = false;
        lock (_sync)
        {
            if (_disposed)
            {
                failureFeedback = ServiceClosedFailure().Feedback;
                return null;
            }

            var (selected, revision) = _sessionState.CaptureSelectedConnection();
            if (revision != token.SelectionRevision
                || !ReferenceEquals(selected, activeConnection)
                || !ReferenceEquals(_loadedConnection, activeConnection)
                || !IsCurrentConnectionLocked(activeConnection, generation))
            {
                failureOutcome = TopicExcludeAddOutcome.SavedButNotApplied;
                failureFeedback = SavedButNotAppliedFailure().Feedback;
                return null;
            }

            if (rule is null)
            {
                _topics.Add(filter);
                return RequestPurgeLocked(filter, generation, out deferred);
            }

            _sparkplugRules.Add(rule);
            return RequestPurgeLocked(rule, generation, out deferred);
        }
    }

    private static bool IsValidSparkplugExclude(SparkplugTopicExclude? rule) =>
        rule is not null
        && TryNormalizeBranch(
            new TopicBranchTarget(TopicBranchKind.Sparkplug, Scope: rule.Scope, MessageType: rule.MessageType),
            out _, out _, out _);

    private static bool TryNormalizeBranch(
        TopicBranchTarget? target, out string filter,
        out SparkplugTopicExclude? rule, out UserNotification? feedback)
    {
        filter = string.Empty;
        rule = null;
        feedback = null;
        if (target is null)
        {
            feedback = Invalid("Invalid topic exclusion").Feedback;
            return false;
        }

        return target.Kind switch
        {
            TopicBranchKind.Raw => TryNormalizeRaw(target, out filter, out feedback),
            TopicBranchKind.Sparkplug => TryNormalizeSparkplug(target, out rule, out feedback),
            _ => InvalidBranch(out feedback)
        };
    }

    private static bool InvalidBranch(out UserNotification? feedback)
    {
        feedback = Invalid("Invalid topic exclusion").Feedback;
        return false;
    }

    private static bool TryNormalizeRaw(
        TopicBranchTarget target, out string filter, out UserNotification? feedback)
    {
        filter = string.Empty;
        feedback = null;
        if (target.Scope is not null || target.MessageType is not null || target.Path is not { } path)
        {
            feedback = Invalid("Invalid topic exclusion").Feedback;
            return false;
        }

        if (path.Length > 0 && !MqttTopicMatcher.IsValidTopicName(path))
        {
            feedback = Invalid("Invalid topic exclusion").Feedback;
            return false;
        }

        filter = path + "/#";
        if (!MqttTopicMatcher.IsValidFilter(filter))
        {
            feedback = Invalid("Invalid topic exclusion").Feedback;
            return false;
        }

        return true;
    }

    private static bool TryNormalizeSparkplug(
        TopicBranchTarget target, out SparkplugTopicExclude? rule, out UserNotification? feedback)
    {
        rule = null;
        feedback = null;
        if (target.Path is not null || target.Scope is not { } scope || !IsValidScope(scope))
        {
            feedback = Invalid("Invalid Sparkplug exclusion").Feedback;
            return false;
        }

        if (target.MessageType is { } messageType && !IsValidMessageType(scope.Kind, messageType))
        {
            feedback = Invalid("Invalid Sparkplug exclusion").Feedback;
            return false;
        }

        rule = new(scope, target.MessageType);
        return true;
    }

    private static bool IsValidScope(SparkplugSelectionScope scope)
    {
        if (scope.Kind is not (SparkplugSelectionKind.Node or SparkplugSelectionKind.Devices
            or SparkplugSelectionKind.Device))
            return false;

        if (!IsValidGroupPath(scope.GroupPath) || !IsValidLevel(scope.NodeId))
            return false;

        return scope.Kind switch
        {
            SparkplugSelectionKind.Device => IsValidLevel(scope.DeviceId),
            _ => scope.DeviceId is null
        };
    }

    private static bool IsValidGroupPath(string? groupPath)
    {
        if (groupPath is null || !MqttStringRules.IsValidUtf8String(groupPath))
            return false;

        var segments = groupPath.Split('/');
        return segments.Length == 2
               && string.Equals(segments[0], SparkplugTopicClassifier.Namespace, StringComparison.Ordinal)
               && IsValidLevel(segments[1]);
    }

    private static bool IsValidLevel(string? value) =>
        !string.IsNullOrEmpty(value)
        && !value.Contains('/')
        && !value.Contains('+')
        && !value.Contains('#')
        && MqttStringRules.IsValidUtf8String(value);

    private static bool IsValidMessageType(SparkplugSelectionKind kind, string messageType) =>
        kind switch
        {
            SparkplugSelectionKind.Node => SparkplugTopicClassifier.IsNodeVerb(messageType),
            SparkplugSelectionKind.Devices or SparkplugSelectionKind.Device =>
                SparkplugTopicClassifier.IsDeviceVerb(messageType),
            _ => false
        };

    private static UserNotification AlreadyExcludedFeedback(string label) =>
        new(UserNotificationSeverity.Warning, $"Already excluding {label}");

    private static UserNotification LimitReachedFeedback() =>
        new(UserNotificationSeverity.Warning, $"Topic exclusion limit ({MaxTopicExcludes}) reached");
}
