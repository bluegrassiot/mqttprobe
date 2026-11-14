using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace MqttProbe.UI.Components.Browser.Connection;

public partial class ConnectionDialog
{
    private TaskCompletionSource<bool>? _discardDecision;
    private bool _discardPromptVisible;
    private bool _discardClosesDialog;
    private bool _focusDiscardConfirmation;
    private bool _restoreFocusAfterDiscardConfirmation;
    private MudButton? _discardCancelButton;

    private static readonly Dictionary<string, object> _inertBodyAttributes = new(StringComparer.Ordinal) { ["inert"] = "" };
    private static readonly Dictionary<string, object> _activeBodyAttributes = new(StringComparer.Ordinal);

    private Dictionary<string, object> BodyAttributes =>
        _discardPromptVisible ? _inertBodyAttributes : _activeBodyAttributes;

    private async Task<bool> GuardIfDirty(bool closingDialog = false)
    {
        if (!IsDirty) return true;
        if (_discardDecision is not null) return false;

        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _discardDecision = decision;
        _discardClosesDialog = closingDialog;
        _restoreFocusAfterDiscardConfirmation = false;

        if (_viewport is { } viewport)
            await viewport.RememberFocusAsync(_dialogContent);

        if (_disposed || !ReferenceEquals(_discardDecision, decision))
        {
            decision.TrySetResult(false);
            return false;
        }

        _discardPromptVisible = true;
        _focusDiscardConfirmation = true;
        StateHasChanged();

        return await decision.Task;
    }

    private void CancelDiscardPrompt() => ResolveDiscardPrompt(discard: false);

    private void DiscardChanges() => ResolveDiscardPrompt(discard: true);

    private void ResolveDiscardPrompt(bool discard)
    {
        var decision = _discardDecision;
        if (decision is null) return;

        var wasVisible = _discardPromptVisible;
        _discardDecision = null;
        _discardPromptVisible = false;
        _focusDiscardConfirmation = false;
        _restoreFocusAfterDiscardConfirmation = wasVisible && (!discard || !_discardClosesDialog);
        decision.TrySetResult(discard);

        if (wasVisible)
            StateHasChanged();
    }

    private async Task RunDiscardPromptFocusStepAsync()
    {
        if (_focusDiscardConfirmation && _discardCancelButton is not null)
        {
            _focusDiscardConfirmation = false;
            await _discardCancelButton.FocusAsync();
        }

        if (_restoreFocusAfterDiscardConfirmation)
        {
            _restoreFocusAfterDiscardConfirmation = false;
            await RestoreDialogFocusAsync();
        }
    }

    private async Task RestoreDialogFocusAsync()
    {
        if (_viewport is { } viewport && await viewport.RestoreFocusAsync(_dialogContent))
            return;

        await _dialogContent.FocusAsync();
    }
}
