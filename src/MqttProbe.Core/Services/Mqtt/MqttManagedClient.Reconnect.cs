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
        if (!IsEligibleForWork(generation))
        {
            await DisconnectQuietlyAsync(generation.Client).ConfigureAwait(false);
            return;
        }

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
        if (!IsOwnedForWork(generation))
            return Task.CompletedTask;

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

        var priorEpoch = _messageConnectionEpoch.Value;
        _messageConnectionEpoch.Value = generation.AttemptId.Value;
        try
        {
            if (ApplicationMessageReceivedAsync is { } handler)
                await handler(args).ConfigureAwait(false);
        }
        finally
        {
            _messageConnectionEpoch.Value = priorEpoch;
        }
    }
}
