using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class MqttManagedClient
{
    private async Task ResubscribeAsync(Generation generation)
    {
        try
        {
            await ResubscribeCoreAsync(generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Retirement or revocation during resubscribe is expected.
        }
        catch (Exception ex)
        {
            // Ownership lost during the await means retirement, not a synchronisation problem.
            if (!IsOwnedForWork(generation) || generation.Cts.IsCancellationRequested)
                return;

            int subscriptionCount;
            lock (_sync)
                subscriptionCount = _subscriptions.Count;
            _logger?.LogError(ex, "Failed to restore {Count} subscription(s) after connect", subscriptionCount);
            _ = RaiseWhileEligibleAsync(SynchronizingSubscriptionsFailedAsync, new MqttManagedProcessFailedEventArgs(ex),
                nameof(SynchronizingSubscriptionsFailedAsync), generation);
        }
    }

    private async Task ResubscribeCoreAsync(Generation generation)
    {
        List<SemaphoreSlim> mutationGates = [];
        try
        {
            while (true)
            {
                string[] gatedTopics;
                lock (_sync)
                    gatedTopics = _subscriptions.Keys.ToArray();

                mutationGates = GetSubscriptionMutationGates(gatedTopics);
                var acquired = 0;
                try
                {
                    for (; acquired < mutationGates.Count; acquired++)
                        await mutationGates[acquired].WaitAsync(generation.Cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    for (var index = acquired - 1; index >= 0; index--)
                        mutationGates[index].Release();
                    mutationGates = [];
                    throw;
                }

                ConnectionLifetime? lifetime;
                long revision;
                string[] currentTopics;
                List<MqttTopicFilter> filters;
                lock (_sync)
                {
                    lifetime = generation.Lifetime;
                    revision = _subscriptionRevision;
                    filters = _subscriptions.Values.ToList();
                    currentTopics = _subscriptions.Keys.ToArray();
                    if (filters.Count == 0 && lifetime is not null && ReferenceEquals(_current, generation))
                        _subscriptionSnapshotReady = true;
                }

                if (currentTopics.Except(gatedTopics, StringComparer.Ordinal).Any())
                {
                    ReleaseSubscriptionMutationGates(mutationGates);
                    mutationGates = [];
                    continue;
                }

                if (filters.Count == 0 || !IsOwnedForWork(generation) || !_activityGate.IsActive)
                    return;

                if (lifetime is not null)
                    await SubscribeAndConfirmAsync(generation, lifetime, filters, revision, generation.Cts.Token)
                        .ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            ReleaseSubscriptionMutationGates(mutationGates);
        }
    }

    private async Task SubscribeAndConfirmAsync(
        Generation generation,
        ConnectionLifetime lifetime,
        List<MqttTopicFilter> filters,
        long revision,
        CancellationToken cancellationToken)
    {
        var builder = new MqttClientSubscribeOptionsBuilder();
        foreach (var filter in filters)
            builder.WithTopicFilter(filter);

        using var linked = CreatePublishTokenSource(cancellationToken, generation.Cts.Token, lifetime.Cancellation.Token);
        var result = await generation.Client.SubscribeAsync(builder.Build(), linked.Token).ConfigureAwait(false);
        var accepted = result.Items
            .Where(item => IsGrantedSubscribeReason(item.ResultCode.ToString()))
            .Select(item => item.TopicFilter.Topic)
            .ToHashSet(StringComparer.Ordinal);

        var current = false;
        lock (_sync)
        {
            current = ReferenceEquals(_current, generation)
                      && ReferenceEquals(generation.Lifetime, lifetime)
                      && !generation.Retired && !lifetime.Retired
                      && !generation.Cts.IsCancellationRequested
                      && !lifetime.Cancellation.IsCancellationRequested
                      && _activityGate.IsActive
                      && _subscriptionRevision == revision;
            if (current)
            {
                foreach (var filter in filters)
                {
                    if (accepted.Contains(filter.Topic))
                        _confirmedSubscriptions[filter.Topic] = filter;
                    else
                        _confirmedSubscriptions.Remove(filter.Topic);
                }
                _subscriptionSnapshotReady = true;
            }
        }

        if (!current)
            return;

        if (accepted.Count != filters.Count
            || !accepted.SetEquals(filters.Select(filter => filter.Topic)))
            throw new InvalidOperationException(
                $"The broker rejected or did not acknowledge {filters.Count - accepted.Count} subscription(s).");
    }

    private async Task UnsubscribeAndConfirmAsync(
        Generation generation,
        ConnectionLifetime lifetime,
        List<string> topics,
        long revision,
        CancellationToken cancellationToken)
    {
        var builder = new MqttClientUnsubscribeOptionsBuilder();
        foreach (var topic in topics)
            builder.WithTopicFilter(topic);

        using var linked = CreatePublishTokenSource(cancellationToken, generation.Cts.Token, lifetime.Cancellation.Token);
        var result = await generation.Client.UnsubscribeAsync(builder.Build(), linked.Token).ConfigureAwait(false);
        var accepted = result.Items.Count == topics.Count
                       && result.Items.All(item => item.ResultCode.ToString() is "Success" or "NoSubscriptionExisted")
                       && result.Items.Select(item => item.TopicFilter).ToHashSet(StringComparer.Ordinal)
                           .SetEquals(topics);
        lock (_sync)
        {
            if (!ReferenceEquals(_current, generation)
                || !ReferenceEquals(generation.Lifetime, lifetime)
                || generation.Retired || lifetime.Retired
                || generation.Cts.IsCancellationRequested
                || lifetime.Cancellation.IsCancellationRequested
                || !_activityGate.IsActive
                || _subscriptionRevision != revision)
                return;

            if (accepted)
                foreach (var topic in topics)
                    _confirmedSubscriptions.Remove(topic);
            _subscriptionSnapshotReady = accepted;
        }

        if (!accepted)
            throw new InvalidOperationException("The broker rejected an unsubscribe request.");
    }

    private static bool IsGrantedSubscribeReason(string reasonCode) =>
        reasonCode is "GrantedQoS0" or "GrantedQoS1" or "GrantedQoS2";

    private async Task DrainPendingAsync(Generation generation)
    {
        try
        {
            await _drainSemaphore.WaitAsync(generation.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            while (true)
            {
                if (!IsOwnedForWork(generation) || !_activityGate.IsActive)
                    return;

                MqttApplicationMessage message;
                long headRevision;
                lock (_sync)
                {
                    if (_pending.Count == 0)
                        return;
                    message = _pending.Peek();
                    headRevision = _pendingHeadRevision;
                }

                try
                {
                    await generation.Client.PublishAsync(message, generation.Cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to publish queued message for topic {Topic}; will retry on next connect", message.Topic);
                    return;
                }

                lock (_sync)
                {
                    // Revision match pins the exact queue-head occurrence across clears and evictions.
                    if (_pendingHeadRevision == headRevision && _pending.Count > 0)
                    {
                        _pending.Dequeue();
                        _pendingHeadRevision++;
                    }
                }
            }
        }
        finally
        {
            _drainSemaphore.Release();
        }
    }

    private async Task RaiseAsync<TArgs>(Func<TArgs, Task>? handler, TArgs args, string eventName)
    {
        if (handler is null)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                await ((Func<TArgs, Task>)subscriber)(args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "A {EventName} subscriber threw; continuing", eventName);
            }
        }
    }

    // Stops between subscribers once the generation is no longer eligible, so a subscriber
    // that awaits Stop does not let later subscribers or trailing notifications run stale.
    private async Task RaiseWhileEligibleAsync<TArgs>(
        Func<TArgs, Task>? handler, TArgs args, string eventName, Generation generation, bool requireActiveGate = true)
    {
        if (handler is null)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            if (!IsOwnedForWork(generation) || (requireActiveGate && !_activityGate.IsActive))
                return;

            try
            {
                await ((Func<TArgs, Task>)subscriber)(args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "A {EventName} subscriber threw; continuing", eventName);
            }
        }
    }

    private async Task NotifyOutcomeAsync(Generation generation, MqttConnectOutcome outcome)
    {
        await RaiseAsync(ConnectAttemptCompletedAsync, outcome, nameof(ConnectAttemptCompletedAsync))
            .ConfigureAwait(false);

        switch (outcome.Status)
        {
            case MqttConnectStatus.Connected:
                if (generation.ConnectedArgs is { } args && IsEligibleForWork(generation))
                    await NotifyConnectedAsync(generation, args).ConfigureAwait(false);
                break;

            case MqttConnectStatus.Failed:
                // Revalidated after the awaited outcome notification: a subscriber may have
                // retired the generation, and a retired attempt must not report a failure.
                if (IsEligibleForWork(generation))
                {
                    await RaiseWhileEligibleAsync(ConnectingFailedAsync, new MqttConnectingFailedEventArgs(outcome.Exception),
                        nameof(ConnectingFailedAsync), generation).ConfigureAwait(false);
                }
                break;
        }
    }

    private async Task NotifyConnectedAsync(Generation generation, MqttClientConnectedEventArgs args)
    {
        if (!IsEligibleForWork(generation))
            return;

        await RaiseWhileEligibleAsync(ConnectedAsync, args, nameof(ConnectedAsync), generation).ConfigureAwait(false);

        if (!IsEligibleForWork(generation))
            return;

        await RaiseWhileEligibleAsync(ConnectionStateChangedAsync, EventArgs.Empty, nameof(ConnectionStateChangedAsync), generation)
            .ConfigureAwait(false);
    }

    private async Task NotifyReconnectFailureAsync(Generation generation, Exception exception)
    {
        if (!IsEligibleForWork(generation))
            return;

        await RaiseWhileEligibleAsync(ConnectingFailedAsync, new MqttConnectingFailedEventArgs(exception),
            nameof(ConnectingFailedAsync), generation).ConfigureAwait(false);
    }

    private async Task NotifyDisconnectedAsync(Generation generation, MqttClientDisconnectedEventArgs args)
    {
        await RaiseWhileEligibleAsync(DisconnectedAsync, args, nameof(DisconnectedAsync), generation, requireActiveGate: false)
            .ConfigureAwait(false);
        await RaiseWhileEligibleAsync(ConnectionStateChangedAsync, EventArgs.Empty, nameof(ConnectionStateChangedAsync),
            generation, requireActiveGate: false).ConfigureAwait(false);
    }
}
