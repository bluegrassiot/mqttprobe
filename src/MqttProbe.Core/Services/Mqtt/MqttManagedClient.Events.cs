using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class MqttManagedClient
{
    private async Task ResubscribeAsync(Generation generation)
    {
        List<MqttTopicFilter> filters;
        lock (_sync)
        {
            if (_subscriptions.Count == 0)
                return;
            filters = _subscriptions.Values.ToList();
        }

        if (!IsOwnedForWork(generation) || !_activityGate.IsActive)
            return;

        try
        {
            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var filter in filters)
                builder.WithTopicFilter(filter);
            await generation.Client.SubscribeAsync(builder.Build(), generation.Cts.Token).ConfigureAwait(false);
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

            _logger?.LogError(ex, "Failed to restore {Count} subscription(s) after connect", filters.Count);
            _ = RaiseWhileEligibleAsync(SynchronizingSubscriptionsFailedAsync, new MqttManagedProcessFailedEventArgs(ex),
                nameof(SynchronizingSubscriptionsFailedAsync), generation);
        }
    }

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

    private Task RaiseConnectionStateChangedAsync() =>
        RaiseAsync(ConnectionStateChangedAsync, EventArgs.Empty, nameof(ConnectionStateChangedAsync));
}
