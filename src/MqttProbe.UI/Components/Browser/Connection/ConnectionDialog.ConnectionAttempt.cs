using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MudBlazor;

namespace MqttProbe.UI.Components.Browser.Connection;

public partial class ConnectionDialog
{
    private async Task OnConnectingFailed(MqttConnectingFailedEventArgs args)
    {
        _connectionState = ConnectionState.Failed;
        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;
        UxMetrics?.RecordConnectFailure();
        Logger.LogError(args.Exception,
            "Connecting to broker {Host}:{Port} failed. ResultCode={ResultCode}",
            _selectedConnection.Host, _selectedConnection.Port,
            args.ConnectResult?.ResultCode);
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnConnected(MqttClientConnectedEventArgs _)
    {
        if (_pendingAttemptConnection is null)
            return;

        _connectionState = ConnectionState.Connected;
        UxMetrics?.RecordConnectSuccess();
        Logger.LogInformation("Connected to broker {Host}:{Port}", _pendingAttemptConnection.Host, _pendingAttemptConnection.Port);

        if (_pendingAttemptSavedSelector is { Id: var savedId })
        {
            SessionState.LastSuccessfulConnectionId = savedId;
            // Saved connection: clean any prior retained unsaved asset if unreferenced
            await CleanupPriorRetainedAssetIfNeeded(savedId);
        }
        else
        {
            SessionState.LastSuccessfulConnectionId = null;
            // Unsaved attempt: clean prior retained asset if new snapshot no longer references it
            // (covers both certificate-free unsaved success and cert-carrying unsaved success)
            var newSnapshotCertId = _pendingAttemptConnection.ClientCertificateAssetId;
            if (SessionState.RetainedUnsavedAssetId is { } priorAssetId
                && priorAssetId != newSnapshotCertId)
            {
                try { await CertStore.DeleteAsync(SessionState.RetainedUnsavedAssetOwnerId, priorAssetId); } catch { /* best-effort */ }
                SessionState.RetainedUnsavedAssetId = null;
                SessionState.RetainedUnsavedAssetOwnerId = Guid.Empty;
            }
            // Retain staged asset in session memory so dialog disposal does not delete it
            if (_stagedAssetId is not null)
            {
                SessionState.RetainedUnsavedAssetId = _stagedAssetId;
                SessionState.RetainedUnsavedAssetOwnerId = _stagedAssetOwnerId;
                _stagedAssetId = null;
            }
        }
        SessionState.LastSuccessfulConnectionSnapshot = _pendingAttemptConnection.Clone();

        _pendingAttemptConnection = null;
        _pendingAttemptSavedSelector = null;

        await MessageStoreManagerService.Start();
        await ChartDataService.StartAsync();
        await InvokeAsync(() => MudDialog.Close(DialogResult.Ok(true)));
    }

    private async Task CleanupPriorRetainedAssetIfNeeded(Guid? savedConnectionId)
    {
        if (SessionState.RetainedUnsavedAssetId is not { } priorAssetId)
            return;
        // If a saved connection references this asset, keep it
        if (savedConnectionId is { } sid)
        {
            var saved = ConnectionSettings.Connections.FirstOrDefault(c => c.Id == sid);
            if (saved is not null && saved.ClientCertificateAssetId == priorAssetId)
                return;
        }
        try { await CertStore.DeleteAsync(SessionState.RetainedUnsavedAssetOwnerId, priorAssetId); } catch { /* best-effort */ }
        SessionState.RetainedUnsavedAssetId = null;
        SessionState.RetainedUnsavedAssetOwnerId = Guid.Empty;
    }

    private async Task Connect()
    {
        if (!ValidateCertificateState()) return;
        if (!await ValidateForm()) return;
        if (!await PromptSaveIfUnsavedChangesAsync()) return;
        if (!await StopActiveConnectionGracefullyAsync()) return;

        var connection = await PrepareConnectionForAttemptAsync();
        if (connection is null) return;

        _activeCertResource = new CertificateSessionResource();
        MqttManagedClientOptions builtOptions;
        try { builtOptions = await MqttOptionsBuilder.BuildAsync(connection, _activeCertResource); }
        catch (Exception ex)
        {
            _activeCertResource.Dispose();
            _activeCertResource = null;
            _certSection?.SetError(ex.Message);
            StateHasChanged();
            return;
        }

        await ExecuteConnectionAttemptAsync(connection, builtOptions);
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
        finally { _activeCertResource = null; }
        return true;
    }

    private async Task<MqttProbe.Core.Models.Mqtt.Connection?> PrepareConnectionForAttemptAsync()
    {
        var connection = _selectedConnection.Clone();
        if (_certSection is not null && _certSection.HasStagedBytes)
        {
            var stagedId = await _certSection.TryImportStagedAsync(connection.Id, _stagedAssetId);
            if (stagedId is null) { StateHasChanged(); return null; }
            connection.ClientCertificateAssetId = stagedId;
            _stagedAssetId = stagedId;
            _stagedAssetOwnerId = connection.Id;
        }
        try { await BrokerResetCoordinator.ResetIfBrokerChangedAsync(connection); }
        catch (Exception ex) { Logger.LogError(ex, "Broker state reset failed; proceeding with connect"); }
        return connection;
    }

    private async Task ExecuteConnectionAttemptAsync(MqttProbe.Core.Models.Mqtt.Connection connection, MqttManagedClientOptions builtOptions)
    {
        try
        {
            UxMetrics?.RecordConnectAttempt();
            _connectionState = ConnectionState.Connecting;
            _pendingAttemptConnection = connection.Clone();
            _pendingAttemptSavedSelector = _savedConnectionForSelector;
            SessionState.SelectedConnection = connection;
            if (_activeCertResource is not null)
            {
                SessionState.ActiveCertificateResource = _activeCertResource;
                _activeCertResource = null;
            }
            // Start capture before StartAsync so handlers are listening when retained messages arrive.
            await MessageStoreManagerService.Start();
            await ChartDataService.StartAsync();
            await ManagedMqttClient.StartAsync(builtOptions);
        }
        catch (Exception ex)
        {
            _connectionState = ConnectionState.Failed;
            _pendingAttemptConnection = null;
            _pendingAttemptSavedSelector = null;
            Logger.LogError(ex, "Failed to connect.");
            try { await SessionLifecycle.StopActiveConnectionAsync(); }
            catch (Exception stopEx) { Logger.LogError(stopEx, "StopActiveConnectionAsync also failed after StartAsync failure"); }
        }
        StateHasChanged();
    }
}
