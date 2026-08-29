using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MudBlazor;

namespace MqttProbe.UI.Components.Browser.Connection;

public partial class ConnectionDialog
{
    private async Task<bool> Save(bool closeDialog = true)
    {
        if (!await ValidateForm()) return false;
        var trimmedName = _selectedConnection.Name.Trim();
        _selectedConnection.Name = trimmedName;
        CheckNameUniqueness();
        if (_nameCollisionError is not null) { StateHasChanged(); return false; }
        var connection = _selectedConnection.Clone();
        var previousAssetId = _certSection?.PreviousAssetId;
        string? stagedAssetId = null;
        if (_certSection is not null && _certSection.HasStagedBytes)
        {
            stagedAssetId = await _certSection.TryImportStagedAsync(connection.Id, _stagedAssetId);
            if (stagedAssetId is null) { StateHasChanged(); return false; }
            _stagedAssetId = stagedAssetId;
            _stagedAssetOwnerId = connection.Id;
            connection.ClientCertificateAssetId = stagedAssetId;
        }
        try { await ConnectionSettings.AddConnectionAsync(connection); }
        catch (Exception ex)
        {
            if (stagedAssetId is not null)
            {
                try { await CertStore.DeleteAsync(connection.Id, stagedAssetId); } catch { /* save failed */ }
                _stagedAssetId = null;
            }
            _certSection?.SetError($"Failed to save: {ex.Message}");
            StateHasChanged();
            return false;
        }
        await FinalizeSave(connection, previousAssetId, stagedAssetId);
        if (closeDialog) MudDialog.Close(DialogResult.Ok(connection));
        return true;
    }

    private async Task FinalizeSave(MqttProbe.Core.Models.Mqtt.Connection connection, string? previousAssetId, string? stagedAssetId)
    {
        if (previousAssetId is not null)
        {
            bool replacedByNew = stagedAssetId is not null && previousAssetId != stagedAssetId;
            bool removedByUser = stagedAssetId is null && connection.ClientCertificateAssetId is null;
            if (replacedByNew || removedByUser)
                try { await CertStore.DeleteAsync(connection.Id, previousAssetId); } catch { /* replaced asset */ }
        }
        _stagedAssetId = null;
        _certSection?.ClearStagedAfterPersist(connection.ClientCertificateAssetId);
        // If the persisted asset was previously retained as unsaved, clear retained metadata
        if (connection.ClientCertificateAssetId is not null
            && SessionState.RetainedUnsavedAssetId == connection.ClientCertificateAssetId)
        {
            SessionState.RetainedUnsavedAssetId = null;
            SessionState.RetainedUnsavedAssetOwnerId = Guid.Empty;
        }
        Logger.LogInformation("Connection saved: {Name} ({Host}:{Port})", connection.Name, connection.Host, connection.Port);
        _savedConnectionForSelector = ConnectionSettings.Connections.FirstOrDefault(c => c.Id == connection.Id) ?? connection;
        _selectedConnection = _savedConnectionForSelector.Clone();
        _canDelete = true;
        CaptureBaseline();
        UpdateActionStates();
    }

    internal async Task ConnectionChanged(MqttProbe.Core.Models.Mqtt.Connection value)
    {
        var previousSelector = _savedConnectionForSelector;
        if (!await GuardIfDirty())
        {
            _savedConnectionForSelector = previousSelector;
            _selectorGeneration++;
            await InvokeAsync(StateHasChanged);
            return;
        }
        await CleanupStagedAsset();
        await _form.ResetValidationAsync();
        _savedConnectionForSelector = value;
        _selectedConnection = value.Clone();
        _formReportsValid = true;
        _canDelete = true;
        await RefreshCertSectionForConnection();
        CaptureBaseline();
        UpdateActionStates();
        await InvokeAsync(StateHasChanged);
    }

    private async Task RefreshCertSectionForConnection()
    {
        var previousAssetId = _selectedConnection.ClientCertificateAssetId;
        var unavailable = false;
        string? certError = null;
        if (previousAssetId is not null)
        {
            var bundle = await CertStore.LoadAsync(_selectedConnection.Id, previousAssetId);
            if (bundle is null)
            {
                certError = "The configured client certificate is unavailable or corrupt. " +
                    "Re-import the certificate or remove it from the connection settings.";
                unavailable = true;
            }
            else { bundle.Certificate.Dispose(); }
        }
        _certSection?.ResetForConnection(previousAssetId, unavailable, certError);
    }

    private async Task Delete()
    {
        if (!await GuardIfDirty()) return;
        Logger.LogInformation("Connection deleted: {Name} ({Host}:{Port})", _selectedConnection.Name, _selectedConnection.Host, _selectedConnection.Port);
        await CleanupStagedAsset();
        await ConnectionSettings.RemoveConnectionAsync(_selectedConnection);
        ResetToNew();
    }

    private async Task CleanupStagedAsset()
    {
        if (_stagedAssetId is not null)
        {
            try { await CertStore.DeleteAsync(_stagedAssetOwnerId, _stagedAssetId); } catch { /* cleanup best-effort */ }
            _stagedAssetId = null;
        }
    }

    private async Task HandleCertStagedAssetCleanup(string? assetId)
    {
        if (assetId is not null && _stagedAssetId == assetId)
        {
            try { await CertStore.DeleteAsync(_stagedAssetOwnerId, _stagedAssetId); } catch { /* cleanup best-effort */ }
            _stagedAssetId = null;
            UpdateActionStates();
        }
    }

    private async Task HandleCertRevert()
    {
        // When the user reverts certificate changes in the child section,
        // clean up any staged asset that was imported during a connect attempt.
        await CleanupStagedAsset();
        UpdateActionStates();
    }

    private async Task Add()
    {
        if (!await GuardIfDirty()) return;
        await CleanupStagedAsset();
        ResetToNew();
    }

    private void ResetToNew()
    {
        _savedConnectionForSelector = null;
        _selectedConnection = new MqttProbe.Core.Models.Mqtt.Connection();
        _activeTabIndex = 0;
        _formReportsValid = true;
        _canDelete = false;
        _stagedAssetId = null;
        _certSection?.ResetForConnection(null);
        CaptureBaseline();
        UpdateActionStates();
    }

    private async Task CopyConnection()
    {
        var sourceName = _selectedConnection.Name.Trim();
        if (string.IsNullOrWhiteSpace(sourceName)) return;

        if (!await ValidateForm())
        {
            Snackbar.Add("Fix validation errors before copying.", Severity.Warning);
            return;
        }

        var trimmedNewName = await PromptCopyNameAsync(sourceName);
        if (trimmedNewName is null) return;

        var (copy, newCopyAssetId) = await BuildCopyWithCertAsync(trimmedNewName);
        if (copy is null) return;

        try { await ConnectionSettings.AddConnectionAsync(copy); }
        catch (Exception ex)
        {
            if (newCopyAssetId is not null)
                try { await CertStore.DeleteAsync(copy.Id, newCopyAssetId); } catch { /* copy failed cleanup */ }
            Snackbar.Add($"Copy failed: {ex.Message}", Severity.Error);
            return;
        }

        await FinalizeCopy(copy);
    }

    private async Task<string?> PromptCopyNameAsync(string sourceName)
    {
        var uniqueName = GenerateCopyName(sourceName);
        var existingNames = ConnectionSettings.Connections.Select(c => c.Name).ToList();
        var promptDialogRef = await ShowCopyPromptAsync(uniqueName, existingNames);
        if (promptDialogRef is null) return null;

        var result = await promptDialogRef.Result;
        if (result?.Canceled != false || result.Data is not string newName || string.IsNullOrWhiteSpace(newName))
            return null;

        var trimmedNewName = newName.Trim();

        var collision = ConnectionSettings.Connections.Any(c =>
            string.Equals(c.Name.Trim(), trimmedNewName, StringComparison.OrdinalIgnoreCase));
        if (collision)
        {
            Snackbar.Add($"A connection named \"{trimmedNewName}\" already exists.", Severity.Warning);
            return null;
        }
        return trimmedNewName;
    }

    private async Task<(MqttProbe.Core.Models.Mqtt.Connection? copy, string? assetId)> BuildCopyWithCertAsync(string trimmedNewName)
    {
        var source = _selectedConnection.Clone();
        var copy = new MqttProbe.Core.Models.Mqtt.Connection
        {
            Name = trimmedNewName,
            Host = source.Host,
            Port = source.Port,
            User = source.User,
            Password = source.Password,
            Protocol = source.Protocol,
            MqttVersion = source.MqttVersion,
            ClientId = source.ClientId,
            WebsocketBasePath = source.WebsocketBasePath,
            UseTls = source.UseTls,
            AllowUntrustedCertificate = source.AllowUntrustedCertificate,
            ConnectTimeout = source.ConnectTimeout,
            ReconnectDelay = source.ReconnectDelay,
            KeepAlivePeriod = source.KeepAlivePeriod,
            CleanStart = source.CleanStart,
            SessionExpiryIntervalSeconds = source.SessionExpiryIntervalSeconds,
            SubscribedTopics = source.SubscribedTopics.Select(t => new SubscribedTopic { Topic = t.Topic, QualityOfServiceLevel = t.QualityOfServiceLevel }).ToList(),
            TopicExcludes = [.. source.TopicExcludes]
        };

        string? newCopyAssetId = null;
        if (_certSection is not null && _certSection.HasStagedBytes)
        {
            newCopyAssetId = await _certSection.TryImportStagedAsync(copy.Id, null);
            if (newCopyAssetId is null)
            {
                Snackbar.Add("Certificate import failed. Copy aborted.", Severity.Error);
                return (null, null);
            }
            copy.ClientCertificateAssetId = newCopyAssetId;
        }
        else if (_selectedConnection.ClientCertificateAssetId is not null)
        {
            newCopyAssetId = await CertStore.DuplicateAsync(
                _selectedConnection.Id, _selectedConnection.ClientCertificateAssetId, copy.Id);
            if (newCopyAssetId is null)
            {
                Snackbar.Add("Certificate duplication failed. Copy aborted.", Severity.Error);
                return (null, null);
            }
            copy.ClientCertificateAssetId = newCopyAssetId;
        }
        return (copy, newCopyAssetId);
    }

    private async Task FinalizeCopy(MqttProbe.Core.Models.Mqtt.Connection copy)
    {
        _savedConnectionForSelector = ConnectionSettings.Connections.FirstOrDefault(c => c.Id == copy.Id) ?? copy;
        _selectedConnection = _savedConnectionForSelector.Clone();
        _savedConnectionForSelector = _selectedConnection;
        _activeTabIndex = 0;
        _canDelete = true;
        await CleanupStagedAsset();
        _certSection?.ResetForConnection(copy.ClientCertificateAssetId);
        CaptureBaseline();
        UpdateActionStates();
        await InvokeAsync(StateHasChanged);
    }

    private async Task<IDialogReference?> ShowCopyPromptAsync(string suggestedName, IReadOnlyList<string> existingNames)
    {
        var parameters = new DialogParameters<CopyNamePromptDialog>
        {
            { x => x.SuggestedName, suggestedName },
            { x => x.ExistingNames, existingNames }
        };
        var options = new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true };
        return await DialogService.ShowAsync<CopyNamePromptDialog>("Make a copy", parameters, options);
    }

    private string GenerateCopyName(string sourceName)
    {
        var baseName = $"{sourceName} copy";
        var candidate = baseName;
        var counter = 2;
        while (ConnectionSettings.Connections.Any(c =>
            string.Equals(c.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} {counter}";
            counter++;
        }
        return candidate;
    }
}
