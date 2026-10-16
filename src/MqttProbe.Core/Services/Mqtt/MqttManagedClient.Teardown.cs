using Microsoft.Extensions.Logging;
using MQTTnet;

namespace MqttProbe.Core.Services.Mqtt;

public sealed partial class MqttManagedClient
{
    private async Task NotifyDeliberateStopAsync(Generation generation)
    {
        bool notify;
        lock (_sync)
        {
            // A canceled outcome never reported success, so it has no established session to close:
            // the raw connect handler may have run before the failure, but that is not a session.
            notify = generation.WasConnected
                && generation.SettledOutcome?.Status != MqttConnectStatus.Canceled
                && !generation.StopNotified;
            if (notify)
                generation.StopNotified = true;
        }

        if (!notify)
            return;

        await RaiseUnlessSupersededAsync(DisconnectedAsync, StopDisconnectedArgs(), nameof(DisconnectedAsync), generation)
            .ConfigureAwait(false);
        await RaiseUnlessSupersededAsync(ConnectionStateChangedAsync, EventArgs.Empty, nameof(ConnectionStateChangedAsync), generation)
            .ConfigureAwait(false);
    }

    private bool HasNewerAttempt(Generation generation) =>
        Volatile.Read(ref _nextAttemptId) > generation.AttemptId.Value;

    private async Task RaiseUnlessSupersededAsync<TArgs>(
        Func<TArgs, Task>? handler, TArgs args, string eventName, Generation generation)
    {
        if (handler is null)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            if (HasNewerAttempt(generation))
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

    private static MqttClientDisconnectedEventArgs StopDisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger?.LogDebug(ex, "Cancellation token source already disposed during teardown");
        }
    }

    private static async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Cleanup only needs the work finished; the outcome was reported elsewhere.
        }
    }

    private async Task DisconnectQuietlyAsync(IMqttClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error while disconnecting MQTT client during cleanup");
        }
    }

    private void DisposeQuietly(IMqttClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error while disposing MQTT client during cleanup");
        }
    }

    private static CancellationTokenSource LinkToken(CancellationToken first, CancellationToken second)
    {
        if (first.CanBeCanceled && second.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(first, second);
        if (first.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(first);
        if (second.CanBeCanceled)
            return CancellationTokenSource.CreateLinkedTokenSource(second);
        return new CancellationTokenSource();
    }
}
