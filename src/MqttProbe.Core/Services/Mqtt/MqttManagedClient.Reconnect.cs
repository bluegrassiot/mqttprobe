using MQTTnet;
using MQTTnet.Exceptions;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class MqttManagedClient
{
    private void StartReconnectLoop(Generation generation)
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (!IsOwnedForWorkLocked(generation))
                return;
            if (!generation.ReconnectTask.IsCompleted)
                return;

            // Publish the task before the loop can reach the raw connect handler, so cleanup
            // waits for this reconnect operation rather than a completed predecessor.
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            generation.ReconnectTask = completion.Task;
        }

        _ = RunReconnectLoopAsync(generation, completion);
    }

    private async Task ReconnectLoopAsync(Generation generation)
    {
        var cancellationToken = generation.Cts.Token;
        var delay = generation.Options.AutoReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsOwnedForWork(generation) || generation.Client.IsConnected || !_activityGate.IsActive)
                return;

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!IsOwnedForWork(generation) || !_activityGate.IsActive)
                return;

            try
            {
                await generation.ReconnectDrainTask.ConfigureAwait(false);
                if (!IsOwnedForWork(generation) || !_activityGate.IsActive)
                    return;
                var result = await generation.Client.ConnectAsync(generation.Options.ClientOptions, cancellationToken)
                    .ConfigureAwait(false);
                EnsureConnectAccepted(result);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!IsEligibleForWork(generation) || cancellationToken.IsCancellationRequested)
                    return;

                _ = NotifyReconnectFailureAsync(generation, ex);
            }
        }
    }

    private static void EnsureConnectAccepted(MqttClientConnectResult result)
    {
        if (result.ResultCode != MqttClientConnectResultCode.Success)
            throw new MqttCommunicationException($"MQTT connection rejected: {result.ResultCode}.");
    }

    private async Task OnGenerationConnectedAsync(Generation generation, MqttClientConnectedEventArgs args)
    {
        ConnectionLifetime? previousLifetime;
        lock (_sync)
        {
            if (!IsOwnedForWorkLocked(generation) || !_activityGate.IsActive)
            {
                previousLifetime = null;
            }
            else
            {
                previousLifetime = generation.Lifetime;
                if (previousLifetime is not null)
                    previousLifetime.Retired = true;
                var lifetime = new ConnectionLifetime(++_nextConnectionEpoch, _subscriptionRevision);
                generation.Lifetime = lifetime;
                generation.Lifetimes.Add(lifetime);
                _confirmedSubscriptions.Clear();
                _subscriptionSnapshotReady = _subscriptions.Count == 0;
            }
        }

        if (!IsEligibleForWork(generation))
        {
            await DisconnectQuietlyAsync(generation.Client).ConfigureAwait(false);
            return;
        }

        if (previousLifetime is not null)
            CancelQuietly(previousLifetime.Cancellation);

        await ResubscribeAsync(generation).ConfigureAwait(false);
        await DrainPendingAsync(generation).ConfigureAwait(false);

        if (!IsEligibleForWork(generation))
        {
            await DisconnectQuietlyAsync(generation.Client).ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            if (IsOwnedForWorkLocked(generation) && _activityGate.IsActive)
                generation.WasConnected = true;
        }

        // Notify outside the raw connect operation because subscribers may await Stop.
        if (generation.ConnectCompleted.Task.IsCompleted)
            _ = NotifyConnectedAsync(generation, args);
        else
            generation.ConnectedArgs = args;
    }

    private Task OnGenerationDisconnectedAsync(Generation generation, MqttClientDisconnectedEventArgs args)
    {
        ConnectionLifetime? lifetime;
        lock (_sync)
        {
            if (!IsOwnedForWorkLocked(generation))
                return Task.CompletedTask;
            lifetime = generation.Lifetime;
            if (lifetime is not null)
            {
                lifetime.Retired = true;
                generation.ReconnectDrainTask = Task.WhenAll(generation.ReconnectDrainTask, lifetime.DrainTask);
            }
            generation.Lifetime = null;
            _subscriptionRevision++;
            _subscriptionSnapshotReady = false;
            _confirmedSubscriptions.Clear();
        }

        if (lifetime is not null)
            CancelQuietly(lifetime.Cancellation);

        if (_activityGate.IsActive)
            StartReconnectLoop(generation);

        _ = NotifyDisconnectedAsync(generation, args);
        return Task.CompletedTask;
    }

    private async Task OnGenerationApplicationMessageReceivedAsync(
        Generation generation, MqttApplicationMessageReceivedEventArgs args)
    {
        if (!IsOwnedForWork(generation) || !_activityGate.IsActive)
            return;

        long epoch;
        MqttSubscriptionSnapshot? snapshot;
        lock (_sync)
        {
            if (generation.Lifetime is not { Retired: false } lifetime)
                return;
            epoch = lifetime.Epoch;
            snapshot = CreateLiveSubscriptionSnapshotLocked();
        }

        var priorContext = _inboundContext.Value;
        _inboundContext.Value = new InboundContext(epoch, snapshot);
        try
        {
            if (ApplicationMessageReceivedAsync is { } handler)
                await handler(args).ConfigureAwait(false);
        }
        finally
        {
            _inboundContext.Value = priorContext;
        }
    }
}
