using MQTTnet;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.UI.Components.Browser.Connection;

public partial class ConnectionDialog
{
    private bool AttemptAlreadyConnected(ConnectOperation operation) =>
        operation.AttemptStarted
        && ManagedMqttClient.IsCurrentAttempt(operation.AttemptId)
        && ManagedMqttClient.IsConnected;

    // The listener is bound to the operation that subscribed it, so a notification can never
    // target a later attempt. Ownership is still decided by the finalizer, not here, because
    // this runs off the renderer and a queued callback can find the attempt already gone.
    private Func<MqttClientConnectedEventArgs, Task> ConnectedListenerFor(ConnectOperation operation) =>
        async _ =>
        {
            if (_disposed || operation.Completed || operation.Cancelled || operation.ConnectedHandled)
                return;

            await InvokeAsync(() => FinalizeConnectedAttemptAsync(operation, requireConnected: true));
        };

    private async Task<bool> ListenAndReconcileAttemptAsync(ConnectOperation operation, MqttConnectAttempt attempt)
    {
        operation.AttemptId = attempt.Id;
        operation.ConnectedHandler = ConnectedListenerFor(operation);
        ManagedMqttClient.ConnectedAsync += operation.ConnectedHandler;

        // A reconnect can land before this attempt is observable, so the published id is
        // reconciled against the live client instead of waiting on a notification already gone.
        if (!AttemptAlreadyConnected(operation))
            return false;

        var accepted = false;
        await InvokeAsync(async () =>
            accepted = await FinalizeConnectedAttemptAsync(operation, requireConnected: true));
        return accepted;
    }

    // Both the connect notification and the reconciliation paths land here, and this is the
    // acceptance boundary: past it the connection is accepted, so a cancel or dispose that
    // arrives afterwards must not stop it, and finalization owns the certificate handover.
    // A settled connect outcome carries its own proof of connection, so only the paths that
    // start from a notification or from reconciled client state require a live connection.
    private async Task<bool> FinalizeConnectedAttemptAsync(ConnectOperation operation, bool requireConnected = false)
    {
        if (operation.Completed || operation.Cancelled || operation.ConnectedHandled || operation.Finalizing)
            return false;
        if (!IsCurrent(operation) || _pendingAttemptConnection is null)
            return false;
        if (!operation.AttemptStarted || !ManagedMqttClient.IsCurrentAttempt(operation.AttemptId))
        {
            AbandonStaleAttempt(operation);
            return false;
        }
        // The generation still owns the attempt but the link is down: it is still retrying, so
        // the attempt keeps its snapshot and listener instead of being abandoned.
        if (requireConnected && !ManagedMqttClient.IsConnected)
            return false;

        operation.ConnectedHandled = true;
        operation.Finalizing = true;
        await HandleConnectedAttemptAsync(operation);
        return true;
    }

    private void AbandonStaleAttempt(ConnectOperation operation)
    {
        if (operation.Completed || !ReferenceEquals(_operation, operation))
            return;

        _connectionState = ConnectionState.Disconnected;
        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;
        ReleaseOperation(operation);
        if (!_disposed)
            StateHasChanged();
    }
}
