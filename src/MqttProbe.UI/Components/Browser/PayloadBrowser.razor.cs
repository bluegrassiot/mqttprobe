using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

public partial class PayloadBrowser
{
    private Timer? _updateTimer;
    private int _queryInFlight;
    private SelectedTopicToken? _lastAppliedToken;
    private SelectedTopicState? _appliedState;
    private int _lastAppliedMaxCount;

    private readonly string _domIdSuffix = Guid.NewGuid().ToString("N")[..8];
    private ElementReference _listElement;
    private ElementReference _detailElement;
    private MessageListKeyboardInterop? _keyboard;
    private Guid? _activeMessageId;
    private int _activeIndex = -1;
    private FocusTarget _pendingFocus;
    private bool _revealPending;
    private bool _listAttached;
    private string? _visibleForSearch;
    private List<MqttMessage>? _visibleMessages;

    private bool ListHasKeyboardFocus { get; set; }

    [Inject] private ILogger<PayloadBrowser> Logger { get; set; } = null!;

    [Inject] private IJSRuntime Js { get; set; } = null!;

    private enum FocusTarget { None, List, Detail }

    private string? ActiveDescendantId =>
        _activeMessageId is { } id && ActiveIndex() >= 0 ? RowDomId(id) : null;

    private string? AppliedTopicKey => _appliedState?.Key;

    private bool IsAppliedSelectionCurrent() =>
        _appliedState is { } applied && applied.Token.IsSameSelection(MessageStoreManager.GetSelectedTopicState().Token);

    private MessageListKeyboardInterop Keyboard => _keyboard ??= new MessageListKeyboardInterop(Js);

    protected override Task OnInitializedAsync()
    {
        _filterFunc = FilterFunc;
        if (BulkSelection is not null)
            BulkSelection.MessagesCleared += OnMessagesCleared;
        _updateTimer = new Timer(OnTimerTick, null, 0, 500);
        return Task.CompletedTask;
    }

    private void OnMessagesCleared(ImmutableArray<string> topics)
    {
        if (_selectedMessage?.Topic is not { } topic
            || !topics.Contains(topic, StringComparer.Ordinal))
            return;

        _ = InvokeAsync(() =>
        {
            if (Disposed)
                return;

            // The detail view is gone, so the list takes focus and the keyboard state with it.
            ClearSelection();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await SyncListAttachmentAsync();

        if (_revealPending)
        {
            _revealPending = false;
            if (_selectedMessage is null)
                await RevealActiveMessageAsync();
        }

        if (_pendingFocus == FocusTarget.None)
            return;

        var target = _pendingFocus;
        _pendingFocus = FocusTarget.None;
        await FocusTargetAsync(target);
    }

    private async Task SyncListAttachmentAsync()
    {
        var listRendered = _selectedMessage is null && _listElement.Context is not null;
        if (listRendered == _listAttached)
            return;

        _listAttached = listRendered;
        try
        {
            if (listRendered)
                await Keyboard.AttachAsync(_listElement);
            else
                await Keyboard.DetachAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listener with the page.
        }
    }

    private async Task RevealActiveMessageAsync()
    {
        try
        {
            await Keyboard.RevealAsync(_listElement, _activeIndex);
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Message list could not scroll to the keyboard-active message");
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; nothing to scroll.
        }
    }

    private async Task FocusTargetAsync(FocusTarget target)
    {
        var element = target == FocusTarget.List ? _listElement : _detailElement;
        if (element.Context is null)
            return;

        try
        {
            await element.FocusAsync();
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Focus request for the message view failed");
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "The focus target is no longer rendered");
        }
    }

    private List<MqttMessage> VisibleMessages()
    {
        if (_visibleMessages is not null && _visibleForSearch == SearchStringTerm)
            return _visibleMessages;

        _visibleForSearch = SearchStringTerm;
        _visibleMessages = _messageList.Where(FilterFunc).ToList();
        return _visibleMessages;
    }

    private int ActiveIndex() => IndexOfMessage(_activeMessageId);

    private int SelectedIndex() => IndexOfMessage(_selectedMessage?.Id);

    private int IndexOfMessage(Guid? messageId)
    {
        if (messageId is not { } id)
            return -1;

        var visible = VisibleMessages();
        for (var index = 0; index < visible.Count; index++)
        {
            if (visible[index].Id == id)
                return index;
        }

        return -1;
    }

    private string RowDomId(MqttMessage message) => RowDomId(message.Id);

    private string RowDomId(Guid messageId) => $"payload-message-{_domIdSuffix}-{messageId:N}";

    private int RowIndex(MqttMessage message) => VisibleMessages().IndexOf(message);

    private void HandleMessageListKeyDown(KeyboardEventArgs e)
    {
        // Blazor keyboard events carry no target, so owning focus is what keeps a nested control's keys out of the list.
        if (!ListHasKeyboardFocus || e.IsComposing || e.CtrlKey || e.AltKey || e.MetaKey)
            return;

        if (!IsAppliedSelectionCurrent())
            return;

        var visible = VisibleMessages();
        if (visible.Count == 0)
            return;

        switch (e.Key)
        {
            case "ArrowDown":
                SetActiveIndex(Math.Min(ActiveIndex() + 1, visible.Count - 1));
                break;
            case "ArrowUp":
                SetActiveIndex(ActiveIndex() <= 0 ? 0 : ActiveIndex() - 1);
                break;
            case "Home":
                SetActiveIndex(0);
                break;
            case "End":
                SetActiveIndex(visible.Count - 1);
                break;
            case "Enter":
            case " ":
                if (!e.Repeat)
                    OpenActiveMessage();
                break;
        }
    }

    private void HandleDetailKeyDown(KeyboardEventArgs e)
    {
        if (e.IsComposing || e.CtrlKey || e.AltKey || e.MetaKey)
            return;

        switch (e.Key)
        {
            case "ArrowLeft":
                if (IsAppliedSelectionCurrent())
                    SelectPreviousMessage();
                break;
            case "ArrowRight":
                if (IsAppliedSelectionCurrent())
                    SelectNextMessage();
                break;
            case "Escape":
                ClearSelection();
                break;
        }
    }

    private void SetActiveIndex(int index)
    {
        var visible = VisibleMessages();
        if (index < 0 || index >= visible.Count)
            return;

        _activeMessageId = visible[index].Id;
        _activeIndex = index;
        _revealPending = true;
        StateHasChanged();
    }

    private void OpenActiveMessage()
    {
        var index = ActiveIndex();
        if (index < 0)
            return;

        SelectDetailMessage(VisibleMessages()[index], focusDetail: true);
    }

    private void SelectDetailMessage(MqttMessage message, bool focusDetail = false)
    {
        _selectedMessage = message;
        _activeMessageId = message.Id;
        _activeIndex = ActiveIndex();
        if (focusDetail)
            _pendingFocus = FocusTarget.Detail;
        StateHasChanged();
    }

    private void SelectPreviousMessage()
    {
        var index = SelectedIndex();
        if (index <= 0)
            return;

        SelectDetailMessage(VisibleMessages()[index - 1]);
    }

    private void SelectNextMessage()
    {
        var index = SelectedIndex();
        var visible = VisibleMessages();
        if (index < 0 || index >= visible.Count - 1)
            return;

        SelectDetailMessage(visible[index + 1]);
    }

    internal void ClearSelection()
    {
        _selectedMessage = null;
        _jsonTreeView = null;
        _pendingFocus = FocusTarget.List;
        StateHasChanged();
    }

    internal void MessageChanged(MqttMessage? message)
    {
        if (message is null)
        {
            ClearSelection();
            return;
        }

        SelectDetailMessage(message, focusDetail: true);
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
        _visibleMessages = null;
        _selectedMessage = null;
        _activeMessageId = null;
        _activeIndex = -1;
        _lastAppliedToken = null;
        _appliedState = null;
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
        _visibleMessages = null;
        if (_selectedMessage is { } selected)
        {
            var refreshed = _messageList.Find(message => message.Id == selected.Id);
            if (refreshed is not null)
                _selectedMessage = refreshed;
        }

        _activeIndex = ActiveIndex();
        _lastAppliedToken = result.State.Token;
        _appliedState = result.State;
        _lastAppliedMaxCount = requestedLimit;
        Metrics.SetDisplayedMessageCount(_messageList.Count);
        StateHasChanged();
    }

}
