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
            notify = generation.WasConnected && !generation.StopNotified;
            if (notify)
                generation.StopNotified = true;
        }

        if (!notify)
            return;

        await RaiseAsync(DisconnectedAsync, StopDisconnectedArgs(), nameof(DisconnectedAsync)).ConfigureAwait(false);
        await RaiseConnectionStateChangedAsync().ConfigureAwait(false);
    }

    private static MqttClientDisconnectedEventArgs StopDisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down; nothing left to cancel.
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
