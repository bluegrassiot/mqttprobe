using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class TopicExcludeService
{
    private async Task<(TopicExcludeAddOutcome Outcome, UserNotification? Feedback)> AddCore(
        string topic, Connection? boundConnection, long boundGeneration)
    {
        if (boundConnection is null)
        {
            var initialValidation = ValidateAdd(topic);
            if (!initialValidation.IsValid)
                return ClassifyValidation(topic, initialValidation);
        }

        PendingPurge? purge = null;
        var deferred = false;
        if (!await EnterOperationAsync())
            return (TopicExcludeAddOutcome.Failed, ServiceClosedFailure().Feedback);
        try
        {
            if (TryPrepareAdd(topic, boundConnection, boundGeneration,
                    out var activeConnection, out var generation, out var candidate) is { } failure)
                return failure;

            var persistenceError = await PersistTopicsAsync(activeConnection, candidate);
            if (persistenceError is not null)
                return (TopicExcludeAddOutcome.Failed, PersistenceFailure().Feedback);

            lock (_sync)
            {
                // Disposal can land while persistence is awaited. The change stays on the
                // connection (it loads with the next service), but this call must not claim
                // an active filter it can no longer purge or back.
                if (_disposed)
                    return (TopicExcludeAddOutcome.Failed, ServiceClosedFailure().Feedback);

                if (!IsCurrentConnectionLocked(activeConnection, generation))
                    return (TopicExcludeAddOutcome.Failed, ConnectionChangedFailure().Feedback);

                _topics.Add(topic);
                purge = RequestPurgeLocked(topic, generation, out deferred);
            }
        }
        finally
        {
            ReleaseOperationLock();
        }

        if (purge is null)
            return (TopicExcludeAddOutcome.Failed, ConnectionChangedFailure().Feedback);

        return await SettlePurgeAsync(purge, deferred);
    }

    private (TopicExcludeAddOutcome Outcome, UserNotification? Feedback)? TryPrepareAdd(
        string topic, Connection? boundConnection, long boundGeneration,
        out Connection activeConnection, out long generation, out string[] candidate)
    {
        candidate = [];
        lock (_sync)
        {
            activeConnection = boundConnection ?? _sessionState.SelectedConnection;
            generation = boundConnection is null ? _connectionGeneration : boundGeneration;
            if (!ReferenceEquals(_loadedConnection, activeConnection)
                || !IsCurrentConnectionLocked(activeConnection, generation))
                return (TopicExcludeAddOutcome.Failed, ConnectionChangedFailure().Feedback);

            if (!IsValid(topic))
                return (TopicExcludeAddOutcome.Invalid, Invalid("Invalid topic exclusion").Feedback);

            var validation = ValidateAddLocked(topic);
            if (!validation.IsValid)
                return ClassifyValidationLocked(topic, validation);

            candidate = [.. _topics, topic];
            return null;
        }
    }

    private (TopicExcludeAddOutcome Outcome, UserNotification? Feedback) ClassifyValidation(
        string topic, TopicExcludeValidationResult validation)
    {
        if (!IsValid(topic))
            return (TopicExcludeAddOutcome.Invalid, validation.Feedback);

        lock (_sync)
            return ClassifyValidationLocked(topic, validation);
    }

    private (TopicExcludeAddOutcome Outcome, UserNotification? Feedback) ClassifyValidationLocked(
        string topic, TopicExcludeValidationResult validation)
    {
        if (_topics.Contains(topic))
            return (TopicExcludeAddOutcome.AlreadyExcluded, validation.Feedback);

        return _topics.Count >= MaxTopicExcludes
            ? (TopicExcludeAddOutcome.LimitReached, validation.Feedback)
            : (TopicExcludeAddOutcome.Invalid, validation.Feedback);
    }

    // Runs with neither the operation semaphore nor _sync held, so a subscriber that
    // re-enters Add from a notification cannot deadlock against this call.
    private async Task<(TopicExcludeAddOutcome Outcome, UserNotification? Feedback)> SettlePurgeAsync(
        PendingPurge purge, bool deferred)
    {
        var outcome = deferred ? await purge.Completion.Task : FinishPurge(purge);
        return outcome switch
        {
            PurgeOutcome.Completed => (TopicExcludeAddOutcome.Added, null),
            PurgeOutcome.Failed => (TopicExcludeAddOutcome.AppliedWithPurgeFailure, PurgeFailure().Feedback),
            PurgeOutcome.Removed => (TopicExcludeAddOutcome.Failed, ExclusionRemovedFailure().Feedback),
            _ => (TopicExcludeAddOutcome.Failed, ConnectionChangedFailure().Feedback)
        };
    }
}
