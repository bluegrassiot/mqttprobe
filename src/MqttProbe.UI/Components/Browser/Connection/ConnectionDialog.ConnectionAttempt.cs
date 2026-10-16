using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MudBlazor;

namespace MqttProbe.UI.Components.Browser.Connection;

public partial class ConnectionDialog
{
    // Owns one connect flow from before its first await until it is released.
    private sealed class ConnectOperation
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public bool Cancelled { get; set; }

        public bool Building { get; set; }

        public bool Finalizing { get; set; }

        public bool Completed { get; set; }

        public MqttConnectAttemptId AttemptId { get; set; }

        public bool AttemptStarted => !AttemptId.IsNone;

        public MqttProbe.Core.Models.Mqtt.Connection? SavedSelector { get; set; }

        public CertificateSessionResource? Certificate { get; set; }

        public Task? Teardown { get; set; }
    }

    private bool IsCurrent(ConnectOperation operation) =>
        !_disposed && !operation.Cancelled && ReferenceEquals(_operation, operation);

    private void ReleaseOperation(ConnectOperation operation)
    {
        if (operation.Completed)
            return;

        operation.Completed = true;
        if (ReferenceEquals(_operation, operation))
            _operation = null;
        operation.Certificate?.Dispose();
        operation.Certificate = null;
        operation.Cancellation.Dispose();
    }

    private async Task StopOwnedConnectionAsync()
    {
        try
        {
            await SessionLifecycle.StopActiveConnectionAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to stop a cancelled connection attempt");
        }
    }

    private async Task Connect()
    {
        if (_operation is not null)
            return;

        var operation = new ConnectOperation();
        _operation = operation;

        try
        {
            var prepared = await PrepareStartupAsync(operation);
            if (prepared is null || !IsCurrent(operation))
                return;

            await ExecuteConnectionAttemptAsync(operation, prepared.Value.Connection, prepared.Value.Options);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start the connection.");
            AbortStartup(operation);
        }
        finally
        {
            if (ReferenceEquals(_operation, operation))
            {
                if (operation.Teardown is { } teardown)
                    await teardown;
                if (!operation.Completed)
                {
                    if (_connectionState == ConnectionState.Connecting)
                        _connectionState = ConnectionState.Disconnected;
                    ReleaseOperation(operation);
                    if (!_disposed)
                        StateHasChanged();
                }
            }
        }
    }

    private async Task<(MqttProbe.Core.Models.Mqtt.Connection Connection, MqttManagedClientOptions Options)?> PrepareStartupAsync(
        ConnectOperation operation)
    {
        if (!ValidateCertificateState()) return null;
        if (!await ValidateForm()) return null;
        if (!await PromptSaveIfUnsavedChangesAsync()) return null;
        if (!await StopActiveConnectionGracefullyAsync()) return null;
        if (!IsCurrent(operation)) return null;

        _connectionState = ConnectionState.Connecting;
        StateHasChanged();

        var connection = await PrepareConnectionForAttemptAsync(operation);
        if (!IsCurrent(operation) || connection is null)
            return null;

        operation.Certificate = new CertificateSessionResource();
        operation.Building = true;
        try
        {
            var builtOptions = await MqttOptionsBuilder.BuildAsync(connection, operation.Certificate);
            return (connection, builtOptions);
        }
        catch (Exception ex)
        {
            if (IsCurrent(operation))
                _certSection?.SetError(ex.Message);
            return null;
        }
        finally
        {
            operation.Building = false;
        }
    }

    private bool ValidateCertificateState()
    {
        if (SessionState.CertificateSessionFaulted)
        {
            _certSection?.SetError("Cannot reconnect: previous shutdown failed. Restart the app.");
            StateHasChanged();
            return false;
        }
        if (_certSection is not null && _certSection.IsUnavailable && !_certSection.HasStagedBytes)
        {
            _certSection.SetError("The configured client certificate is unavailable or corrupt. " +
                "Re-import the certificate or remove it before connecting.");
            StateHasChanged();
            return false;
        }
        return true;
    }

    private async Task<bool> PromptSaveIfUnsavedChangesAsync()
    {
        if (!_canSave) return true;

        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true };
        var choice = await DialogService.ShowMessageBoxAsync(
            "Unsaved changes",
            "This connection has unsaved changes. Save before connecting?",
            yesText: "Save & Connect", noText: "Connect anyway", cancelText: "Cancel", options);
        if (choice is null) return false;
        if (choice == true)
        {
            if (!await Save(closeDialog: false)) return false;
            _certSection?.ClearStagedFiles();
        }
        return true;
    }

    private async Task<bool> StopActiveConnectionGracefullyAsync()
    {
        try { await SessionLifecycle.StopActiveConnectionAsync(); }
        catch (Exception ex)
        {
            _certSection?.SetError($"Cannot reconnect: previous shutdown failed ({ex.Message}). Restart the app.");
            StateHasChanged();
            return false;
        }
        return true;
    }

    private async Task<MqttProbe.Core.Models.Mqtt.Connection?> PrepareConnectionForAttemptAsync(ConnectOperation operation)
    {
        operation.SavedSelector = _savedConnectionForSelector;
        var connection = _selectedConnection.Clone();
        if (_certSection is not null && _certSection.HasStagedBytes)
        {
            var stagedId = await _certSection.TryImportStagedAsync(connection.Id, _stagedAssetId);
            if (stagedId is null)
            {
                if (IsCurrent(operation))
                    StateHasChanged();
                return null;
            }
            if (!IsCurrent(operation))
            {
                try { await CertStore.DeleteAsync(connection.Id, stagedId); } catch { /* best-effort */ }
                return null;
            }
            connection.ClientCertificateAssetId = stagedId;
            _stagedAssetId = stagedId;
            _stagedAssetOwnerId = connection.Id;
        }
        try { await BrokerResetCoordinator.ResetIfBrokerChangedAsync(connection); }
        catch (Exception ex) { Logger.LogError(ex, "Broker state reset failed; proceeding with connect"); }
        return connection;
    }

    private async Task ExecuteConnectionAttemptAsync(
        ConnectOperation operation, MqttProbe.Core.Models.Mqtt.Connection connection, MqttManagedClientOptions builtOptions)
    {
        UxMetrics?.RecordConnectAttempt();

        try
        {
            await MessageStoreManagerService.Start();
            await ChartDataService.StartAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start message capture for the connection attempt.");
            AbortStartup(operation);
            return;
        }

        if (!IsCurrent(operation))
            return;

        _pendingAttemptConnection = connection.Clone();
        _pendingAttemptSavedSelector = operation.SavedSelector;
        SessionState.SelectedConnection = connection;

        MqttConnectAttempt attempt;
        try
        {
            attempt = ManagedMqttClient.StartAttempt(builtOptions, operation.Cancellation.Token);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start the connection attempt.");
            AbortStartup(operation);
            return;
        }

        if (operation.Certificate is not null)
        {
            SessionState.ActiveCertificateResource = operation.Certificate;
            operation.Certificate = null;
        }
        operation.AttemptId = attempt.Id;

        MqttConnectOutcome outcome;
        try
        {
            outcome = await attempt.Completion;
        }
        catch (Exception ex)
        {
            outcome = new MqttConnectOutcome(attempt.Id, MqttConnectStatus.Failed, ex);
        }

        if (!_disposed)
            await InvokeAsync(() => HandleAttemptOutcomeAsync(operation, outcome));
    }

    private void AbortStartup(ConnectOperation operation)
    {
        if (!IsCurrent(operation))
            return;

        _connectionState = ConnectionState.Failed;
        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;
        UxMetrics?.RecordConnectFailure();
    }

    private async Task HandleAttemptOutcomeAsync(ConnectOperation operation, MqttConnectOutcome outcome)
    {
        _handledOutcomeCount++;
        if (!IsCurrent(operation))
            return;

        switch (outcome.Status)
        {
            case MqttConnectStatus.Connected:
                await HandleConnectedAttemptAsync(operation);
                break;
            case MqttConnectStatus.Failed:
                HandleFailedAttempt(operation, outcome);
                break;
            default:
                HandleCanceledAttempt(operation);
                break;
        }
    }

    private async Task HandleConnectedAttemptAsync(ConnectOperation operation)
    {
        if (_pendingAttemptConnection is not { } connected)
            return;

        operation.Finalizing = true;
        _connectionState = ConnectionState.Connected;

        var savedSelector = _pendingAttemptSavedSelector;
        var stagedAssetId = _stagedAssetId;
        var stagedAssetOwnerId = _stagedAssetOwnerId;
        var priorRetainedId = SessionState.RetainedUnsavedAssetId;
        var priorRetainedOwnerId = SessionState.RetainedUnsavedAssetOwnerId;

        _stagedAssetId = null;
        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;

        SessionState.LastSuccessfulConnectionSnapshot = connected.Clone();
        if (savedSelector is { Id: var savedId })
        {
            SessionState.LastSuccessfulConnectionId = savedId;
        }
        else
        {
            SessionState.LastSuccessfulConnectionId = null;
            if (stagedAssetId is not null)
            {
                SessionState.RetainedUnsavedAssetId = stagedAssetId;
                SessionState.RetainedUnsavedAssetOwnerId = stagedAssetOwnerId;
            }
        }

        UxMetrics?.RecordConnectSuccess();
        Logger.LogInformation("Connected to broker {Host}:{Port}", connected.Host, connected.Port);
        StateHasChanged();

        if (savedSelector is { Id: var savedSelectorId })
        {
            if (stagedAssetId is not null)
            {
                try { await CertStore.DeleteAsync(stagedAssetOwnerId, stagedAssetId); } catch { /* best-effort */ }
            }
            await CleanupPriorRetainedAssetAsync(savedSelectorId, priorRetainedId, priorRetainedOwnerId);
        }
        else if (priorRetainedId is not null && priorRetainedId != connected.ClientCertificateAssetId)
        {
            await CleanupPriorRetainedAssetAsync(null, priorRetainedId, priorRetainedOwnerId);
        }

        await MessageStoreManagerService.Start();
        await ChartDataService.StartAsync();

        if (!_disposed)
            await InvokeAsync(() => MudDialog.Close(DialogResult.Ok(true)));

        ReleaseOperation(operation);
    }

    private async Task CleanupPriorRetainedAssetAsync(Guid? savedConnectionId, string? priorAssetId, Guid priorOwnerId)
    {
        if (priorAssetId is null)
            return;
        if (savedConnectionId is { } sid)
        {
            var saved = ConnectionSettings.Connections.FirstOrDefault(c => c.Id == sid);
            if (saved is not null && saved.ClientCertificateAssetId == priorAssetId)
                return;
        }

        try { await CertStore.DeleteAsync(priorOwnerId, priorAssetId); } catch { /* best-effort */ }
        if (SessionState.RetainedUnsavedAssetId == priorAssetId)
        {
            SessionState.RetainedUnsavedAssetId = null;
            SessionState.RetainedUnsavedAssetOwnerId = Guid.Empty;
        }
    }

    private void HandleFailedAttempt(ConnectOperation operation, MqttConnectOutcome outcome)
    {
        _connectionState = ConnectionState.Failed;
        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;
        UxMetrics?.RecordConnectFailure();
        Logger.LogError(outcome.Exception,
            "Connecting to broker {Host}:{Port} failed. ResultCode={ResultCode}",
            _selectedConnection.Host, _selectedConnection.Port,
            outcome.ConnectResult?.ResultCode);
        ReleaseOperation(operation);
    }

    private void HandleCanceledAttempt(ConnectOperation operation)
    {
        _connectionState = ConnectionState.Disconnected;
        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;
        ReleaseOperation(operation);
    }

    private async Task CancelOperationAsync()
    {
        var operation = _operation;
        if (operation is null || operation.Cancelled || operation.Finalizing)
            return;

        operation.Cancelled = true;
        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;
        _connectionState = ConnectionState.Disconnected;

        // Synchronous cancel retires the core generation before the stop below reads the active client.
#pragma warning disable S6966 // Deliberate synchronous cancel: the ordering here is load-bearing.
        operation.Cancellation.Cancel();
#pragma warning restore S6966

        if (!operation.AttemptStarted)
            return;

        operation.Teardown ??= StopOwnedConnectionAsync();
        await operation.Teardown;
        ReleaseOperation(operation);
    }

    internal async Task CancelConnect()
    {
        await CancelOperationAsync();
        if (!_disposed)
            StateHasChanged();
    }
}
