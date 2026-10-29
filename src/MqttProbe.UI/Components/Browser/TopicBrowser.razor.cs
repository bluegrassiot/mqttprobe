using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace MqttProbe.UI.Components.Browser;

// A row keeps its identity by key, so neither a refresh nor the virtualizer window can strand
// the user on a row that is no longer rendered.
public partial class TopicBrowser
{
    private const float RowHeight = 36;

    // Distinguishes this instance's key interceptor from the other mounted browser's.
    private readonly string _treeId = $"topic-tree-{Guid.NewGuid():N}";
    private ElementReference _treeElement;
    private TopicTreeKeyboardInterop? _keyboard;
    private DotNetObjectReference<TopicBrowser>? _selfReference;
    private string? _attachedTreeId;

    // _activeKey is the tab stop; _focusedKey is the row the user really focused, which a live
    // update still has to follow when the virtualizer unmounts it.
    private string? _activeKey;
    private string? _focusedKey;
    private string? _pendingFocusKey;
    private int _pendingFocusIndex = -1;
    private bool _pendingFocusRestore;
    private bool _interceptorSubscribed;

    // Unmodified presses only: the page must not scroll, and modified keys keep their shortcuts.
    private static readonly KeyInterceptorOptions _treeKeyOptions = new()
    {
        Keys =
        [
            TreeKey("ArrowDown"), TreeKey("ArrowUp"), TreeKey("ArrowLeft"), TreeKey("ArrowRight"),
            TreeKey("Home"), TreeKey("End"), TreeKey("Enter"), TreeKey(" ")
        ]
    };

    [Inject] private IJSRuntime Js { get; set; } = null!;

    [Inject] private IKeyInterceptorService KeyInterceptor { get; set; } = null!;

    private TopicTreeKeyboardInterop Keyboard => _keyboard ??= new TopicTreeKeyboardInterop(Js);

    private DotNetObjectReference<TopicBrowser> SelfReference =>
        _selfReference ??= DotNetObjectReference.Create(this);

    // A child render can drop the focused row without a parent render, so the page reports that
    // loss and the component restores the row it still owns.
    [JSInvokable]
    public Task OnFocusedRowUnmountedAsync() => RestoreFocusedRowAsync();

    // Only the visible rows are rendered, so a committed row outside that window leaves the tree
    // with no tab stop. The page keeps one rendered row tabbable and reports which row it kept, so
    // tab eligibility follows it and the next render cannot take it away again.
    [JSInvokable]
    public Task OnTabStopFallbackAsync(string key) => InvokeAsync(() =>
    {
        if (_disposed || !ContainsRow(key)) return;
        if (string.Equals(_activeKey, key, StringComparison.Ordinal)) return;

        _activeKey = key;
        StateHasChanged();
    });

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await EnsureInterceptorAsync();
        await EnsureKeyboardAttachedAsync();
        await FocusPendingRowAsync();
        await RetainFocusedRowAsync();
    }

    private static KeyOptions TreeKey(string key) => new()
    {
        Key = key,
        SubscribeDown = false,
        PreventDown = "key+none"
    };

    private bool IsActiveRow(TopicTreeRowModel row) =>
        string.Equals(row.FullPath, _activeKey, StringComparison.Ordinal);

    private void OnRowFocused(string key)
    {
        _activeKey = key;
        _focusedKey = key;
    }

    private void ApplyFrame(TopicSelectionFrame? frame)
    {
        if (frame is null) return;

        var previousRows = _visibleRows;
        var previousActive = _activeKey;
        var focusedRowVanished = previousActive is not null
            && string.Equals(previousActive, _focusedKey, StringComparison.Ordinal)
            && !frame.Rows.Any(row => string.Equals(row.FullPath, previousActive, StringComparison.Ordinal));
        _visibleRows = frame.Rows.ToList();
        _rootTopicCount = frame.RootCount;
        _selectedKey = frame.Selection.Key;
        EnsureActiveRow(previousRows, previousActive);

        if (!focusedRowVanished || _activeKey is null) return;

        _focusedKey = _activeKey;
        RequestFocus(_activeKey, restore: true);
    }

    // The roving tab stop outlives a refresh, so only a row that is no longer visible needs
    // a new one.
    private void EnsureActiveRow(List<TopicTreeRowModel> previousRows, string? previousActive)
    {
        if (_visibleRows.Count == 0)
        {
            _activeKey = null;
            return;
        }

        if (ContainsRow(_activeKey)) return;

        if (ContainsRow(_selectedKey))
        {
            _activeKey = _selectedKey;
            return;
        }

        _activeKey = NearestVisibleAncestor(previousRows, previousActive) ?? _visibleRows[0].FullPath;
    }

    private string? NearestVisibleAncestor(List<TopicTreeRowModel> previousRows, string? previousActive)
    {
        var index = previousActive is null
            ? -1
            : previousRows.FindIndex(row => string.Equals(row.FullPath, previousActive, StringComparison.Ordinal));
        if (index < 0) return null;

        var depth = previousRows[index].Depth;
        for (var ancestor = index - 1; ancestor >= 0; ancestor--)
        {
            if (previousRows[ancestor].Depth >= depth) continue;

            if (ContainsRow(previousRows[ancestor].FullPath)) return previousRows[ancestor].FullPath;
            depth = previousRows[ancestor].Depth;
        }

        return null;
    }

    private bool ContainsRow(string? key) =>
        key is not null && _visibleRows.Exists(row => string.Equals(row.FullPath, key, StringComparison.Ordinal));

    private void RequestFocus(string key, bool restore = false)
    {
        _pendingFocusKey = key;
        _pendingFocusRestore = restore;
        _pendingFocusIndex = _visibleRows.FindIndex(row => string.Equals(row.FullPath, key, StringComparison.Ordinal));
    }

    private async Task FocusPendingRowAsync()
    {
        if (_pendingFocusKey is not { } key) return;

        var restore = _pendingFocusRestore;
        _pendingFocusKey = null;
        _pendingFocusRestore = false;
        if (_disposed || _visibleRows.Count == 0) return;

        try
        {
            if (restore)
                await Keyboard.RestoreFocusAsync(_treeElement, key, _pendingFocusIndex, RowHeight);
            else
                await Keyboard.MoveFocusAsync(_treeElement, key, _pendingFocusIndex, RowHeight);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
    }

    // A live update can shift the virtualizer window off the focused row without the row leaving
    // the projection, so the page is asked to keep focus by key after every render.
    private async Task RetainFocusedRowAsync()
    {
        if (_focusedKey is not { } key || _visibleRows.Count == 0) return;

        var index = _visibleRows.FindIndex(row => string.Equals(row.FullPath, key, StringComparison.Ordinal));
        if (index < 0) return;

        try
        {
            await Keyboard.RetainFocusAsync(_treeElement, key, index, RowHeight);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
    }

    private async Task RestoreFocusedRowAsync()
    {
        if (_disposed || _pendingFocusKey is not null) return;
        if (_focusedKey is not { } key || _visibleRows.Count == 0) return;

        var index = _visibleRows.FindIndex(row => string.Equals(row.FullPath, key, StringComparison.Ordinal));
        if (index < 0) return;

        try
        {
            await Keyboard.RestoreFocusAsync(_treeElement, key, index, RowHeight);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
    }

    private async Task EnsureKeyboardAttachedAsync()
    {
        if (_visibleRows.Count == 0)
        {
            await DetachKeyboardAsync();
            return;
        }

        if (string.Equals(_attachedTreeId, _treeElement.Id, StringComparison.Ordinal)) return;

        if (await Keyboard.AttachAsync(_treeElement, _treeId, SelfReference))
            _attachedTreeId = _treeElement.Id;
    }

    private async Task DetachKeyboardAsync()
    {
        if (_attachedTreeId is null) return;

        _attachedTreeId = null;
        try
        {
            await Keyboard.DetachAsync(_treeId);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
    }

    // Scoped to this tree element, so typing in the filter keeps its own key handling.
    private async Task EnsureInterceptorAsync()
    {
        var hasTree = _visibleRows.Count > 0;
        if (hasTree == _interceptorSubscribed) return;

        _interceptorSubscribed = hasTree;
        try
        {
            if (hasTree)
                await KeyInterceptor.SubscribeAsync(_treeId, _treeKeyOptions, keyDown: (IKeyDownObserver?)null);
            else
                await KeyInterceptor.UnsubscribeAsync(_treeId);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the listeners with the page.
        }
    }

    private async Task ReleaseInteropAsync()
    {
        if (_interceptorSubscribed)
        {
            _interceptorSubscribed = false;
            try
            {
                await KeyInterceptor.UnsubscribeAsync(_treeId);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is gone; the browser drops the listeners with the page.
            }
        }

        await DetachKeyboardAsync();
        if (_keyboard is not null)
            await _keyboard.DisposeAsync();

        _selfReference?.Dispose();
        _selfReference = null;
    }

    // Composing keystrokes and modified keys belong to the input method and the browser.
    private static bool IsTreeKey(KeyboardEventArgs e) =>
        !e.IsComposing && !e.CtrlKey && !e.AltKey && !e.MetaKey && !e.ShiftKey;

    private Task OnRowKeyPressed(TopicTreeRowModel row, KeyboardEventArgs e)
    {
        if (!IsTreeKey(e)) return Task.CompletedTask;

        var index = _visibleRows.FindIndex(item => string.Equals(item.FullPath, row.FullPath, StringComparison.Ordinal));
        if (index < 0) return Task.CompletedTask;

        _activeKey = row.FullPath;
        return e.Key switch
        {
            "ArrowDown" => MoveActiveRow(index + 1),
            "ArrowUp" => MoveActiveRow(index - 1),
            "Home" => MoveActiveRow(0),
            "End" => MoveActiveRow(_visibleRows.Count - 1),
            "ArrowRight" => ExpandOrDescend(row, index),
            "ArrowLeft" => CollapseOrAscend(row, index),
            "Enter" or " " => e.Repeat ? Task.CompletedTask : OnRowSelected(row.ToSelection()),
            _ => Task.CompletedTask
        };
    }

    private async Task CollapseOrAscend(TopicTreeRowModel row, int index)
    {
        if (row.HasChildren && row.IsExpanded)
        {
            await ToggleExpand(row.FullPath);
            RequestFocus(row.FullPath);
            return;
        }

        for (var parent = index - 1; parent >= 0; parent--)
        {
            if (_visibleRows[parent].Depth >= row.Depth) continue;

            await MoveActiveRow(parent);
            return;
        }
    }

    private async Task ExpandOrDescend(TopicTreeRowModel row, int index)
    {
        if (row.HasChildren && !row.IsExpanded)
        {
            await ToggleExpand(row.FullPath);
            RequestFocus(row.FullPath);
            return;
        }

        var hasVisibleChild = index + 1 < _visibleRows.Count
            && _visibleRows[index + 1].Depth == row.Depth + 1;
        if (hasVisibleChild)
            await MoveActiveRow(index + 1);
    }

    private Task MoveActiveRow(int index)
    {
        if (index < 0 || index >= _visibleRows.Count) return Task.CompletedTask;

        // Only the tab stop moves here. The row reports real focus, so a move the user cancels
        // before the row renders cannot claim a row the user never reached.
        _activeKey = _visibleRows[index].FullPath;
        RequestFocus(_activeKey);
        return Task.CompletedTask;
    }
}
