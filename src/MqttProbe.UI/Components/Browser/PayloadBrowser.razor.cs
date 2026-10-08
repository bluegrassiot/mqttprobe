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
        catch (Exception ex) when (_disposed)
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
        if (_disposed || Interlocked.Exchange(ref _queryInFlight, 1) != 0)
            return;

        try
        {
            var selection = MessageStoreManager.GetSelectedTopicState();
            if (_disposed)
                return;

            if (string.IsNullOrEmpty(selection.FullTopic))
            {
                await InvokeAsync(ClearIfNoSelection);
                return;
            }

            var limit = MaxMessageCount;
            if (_lastAppliedToken is { } lastApplied
                && IsSameSelection(lastApplied, selection.Token)
                && lastApplied.ContentVersion >= selection.Token.ContentVersion
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
        if (_disposed || !string.IsNullOrEmpty(MessageStoreManager.GetSelectedTopicState().FullTopic))
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
        if (_disposed)
            return;

        var currentSelection = MessageStoreManager.GetSelectedTopicState();
        if (string.IsNullOrEmpty(currentSelection.FullTopic))
        {
            ClearIfNoSelection();
            return;
        }

        if (result is null
            || result.Limit != requestedLimit
            || !IsSameSelection(requestedToken, currentSelection.Token)
            || !IsSameSelection(requestedToken, result.State.Token)
            || MaxMessageCount != requestedLimit)
            return;

        if (_lastAppliedToken is { } lastApplied
            && IsSameSelection(lastApplied, result.State.Token)
            && result.State.Token.ContentVersion < lastApplied.ContentVersion)
            return;

        if (_lastAppliedToken is { } previousToken
            && !IsSameSelection(previousToken, result.State.Token))
            _selectedMessage = null;

        _messageList = _newestFirst
            ? result.Messages.ToList()
            : result.Messages.Reverse().ToList();
        _lastAppliedToken = result.State.Token;
        _lastAppliedMaxCount = requestedLimit;
        Metrics.SetDisplayedMessageCount(_messageList.Count);
        StateHasChanged();
    }

    private static bool IsSameSelection(SelectedTopicToken left, SelectedTopicToken right) =>
        left.StoreId == right.StoreId && left.Generation == right.Generation;
}
