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
        UxMetrics?.RecordConnectFailure();
        Logger.LogError(args.Exception,
            "Connecting to broker {Host}:{Port} failed. ResultCode={ResultCode}",
            _selectedConnection.Host, _selectedConnection.Port,
            args.ConnectResult?.ResultCode);
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnConnected(MqttClientConnectedEventArgs _)
    {
        _connectionState = ConnectionState.Connected;
        UxMetrics?.RecordConnectSuccess();
        Logger.LogInformation("Connected to broker {Host}:{Port}", _selectedConnection.Host, _selectedConnection.Port);
        await MessageStoreManagerService.Start();
        await ChartDataService.StartAsync();
        await InvokeAsync(() => MudDialog.Close(DialogResult.Ok(true)));
    }

#pragma warning disable MA0051 // Method is too long — preserved from original Razor file
    private async Task Connect()
    {
        if (SessionState.CertificateSessionFaulted)
        {
            _certSection?.SetError("Cannot reconnect: previous shutdown failed. Restart the app.");
            StateHasChanged();
            return;
        }
        if (_certSection is not null && _certSection.IsUnavailable && !_certSection.HasStagedBytes)
        {
            _certSection.SetError("The configured client certificate is unavailable or corrupt. " +
                "Re-import the certificate or remove it before connecting.");
            StateHasChanged();
            return;
        }
        if (!await ValidateForm()) return;
        if (_canSave)
        {
            var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true };
            var choice = await DialogService.ShowMessageBoxAsync(
                "Unsaved changes",
                "This connection has unsaved changes. Save before connecting?",
                yesText: "Save & Connect", noText: "Connect anyway", cancelText: "Cancel", options);
            if (choice is null) return;
            if (choice == true)
            {
                if (!await Save(closeDialog: false)) return;
                _certSection?.ClearStagedFiles();
            }
        }
        try { await SessionLifecycle.StopActiveConnectionAsync(); }
        catch (Exception ex)
        {
            _certSection?.SetError($"Cannot reconnect: previous shutdown failed ({ex.Message}). Restart the app.");
            StateHasChanged();
            return;
        }
        finally { _activeCertResource = null; }

        var connection = _selectedConnection.Clone();
        if (_certSection is not null && _certSection.HasStagedBytes)
        {
            var stagedId = await _certSection.TryImportStagedAsync(connection.Id, _stagedAssetId);
            if (stagedId is null) { StateHasChanged(); return; }
            connection.ClientCertificateAssetId = stagedId;
            _stagedAssetId = stagedId;
        }
        try { await BrokerResetCoordinator.ResetIfBrokerChangedAsync(connection); }
        catch (Exception ex) { Logger.LogError(ex, "Broker state reset failed; proceeding with connect"); }

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
        try
        {
            UxMetrics?.RecordConnectAttempt();
            _connectionState = ConnectionState.Connecting;
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
            Logger.LogError(ex, "Failed to connect.");
            try { await SessionLifecycle.StopActiveConnectionAsync(); }
            catch (Exception stopEx) { Logger.LogError(stopEx, "StopActiveConnectionAsync also failed after StartAsync failure"); }
        }
        StateHasChanged();
    }
#pragma warning restore MA0051
}
