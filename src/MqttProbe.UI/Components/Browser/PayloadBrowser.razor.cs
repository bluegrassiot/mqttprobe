using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

public partial class PayloadBrowser
{
    private Timer? _updateTimer;
    private int _queryInFlight;
    private SelectedTopicToken? _lastAppliedToken;
    private int _lastAppliedMaxCount;

    [Inject] private ILogger<PayloadBrowser> Logger { get; set; } = null!;

    protected override Task OnInitializedAsync()
    {
        _filterFunc = FilterFunc;
        _updateTimer = new Timer(OnTimerTick, null, 0, 500);
        return Task.CompletedTask;
    }

    private void OnTimerTick(object? state) => _ = RefreshSafelyAsync();

    private async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshMessagesAsync();
        }
        catch (Exception ex) when (Disposed)
        {
            Logger.LogDebug(ex, "PayloadBrowser was disposed during refresh");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "PayloadBrowser refresh failed");
        }
    }

    private async Task RefreshMessagesAsync()
    {
        if (Disposed || Interlocked.Exchange(ref _queryInFlight, 1) != 0)
            return;

        try
        {
            var selection = MessageStoreManager.GetSelectedTopicState();
            if (Disposed)
                return;

            if (string.IsNullOrEmpty(selection.Key))
            {
                await InvokeAsync(ClearIfNoSelection);
                return;
            }

            var limit = MaxMessageCount;
            if (_lastAppliedToken is { } lastApplied
                && lastApplied.IsContentAtLeastAsNewAs(selection.Token)
                && limit == _lastAppliedMaxCount)
                return;

            var result = await MessageStoreManager.GetSelectedMessagesAsync(selection.Token, limit);
            await InvokeAsync(() => ApplySnapshot(selection.Token, limit, result));
        }
        finally
        {
            Interlocked.Exchange(ref _queryInFlight, 0);
        }
    }

    private void ClearIfNoSelection()
    {
        if (Disposed || !string.IsNullOrEmpty(MessageStoreManager.GetSelectedTopicState().Key))
            return;

        var needsRefresh = _messageList.Count > 0 || _selectedMessage is not null;
        _messageList = [];
        _selectedMessage = null;
        _lastAppliedToken = null;
        _lastAppliedMaxCount = 0;
        Metrics.SetDisplayedMessageCount(0);

        if (needsRefresh)
            StateHasChanged();
    }

    private void ApplySnapshot(
        SelectedTopicToken requestedToken,
        int requestedLimit,
        SelectedMessagesSnapshot? result)
    {
        if (Disposed)
            return;

        var currentSelection = MessageStoreManager.GetSelectedTopicState();
        if (string.IsNullOrEmpty(currentSelection.Key))
        {
            ClearIfNoSelection();
            return;
        }

        if (result is null
            || result.Limit != requestedLimit
            || !requestedToken.IsSameSelection(currentSelection.Token)
            || !requestedToken.IsSameSelection(result.State.Token)
            || MaxMessageCount != requestedLimit)
            return;

        if (_lastAppliedToken is { } lastApplied
            && lastApplied.IsSameSelection(result.State.Token)
            && !result.State.Token.IsContentAtLeastAsNewAs(lastApplied))
            return;

        if (_lastAppliedToken is { } previousToken
            && !previousToken.IsSameSelection(result.State.Token))
            _selectedMessage = null;

        _messageList = _newestFirst
            ? result.Messages.ToList()
            : result.Messages.Reverse().ToList();
        _lastAppliedToken = result.State.Token;
        _lastAppliedMaxCount = requestedLimit;
        Metrics.SetDisplayedMessageCount(_messageList.Count);
        StateHasChanged();
    }

}
