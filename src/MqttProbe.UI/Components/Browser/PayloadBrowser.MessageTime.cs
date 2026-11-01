using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;

namespace MqttProbe.UI.Components.Browser;

public partial class PayloadBrowser
{
    private MessageTimePreferences _preferences = new();
    private MessageTimePresentation _presentation = MessageTimeDisplay.Describe(new MessageTimePreferences());
    private readonly Dictionary<Guid, MessageTimeFormatItem> _clockItems = [];
    private string _zoneLabel = "UTC";
    private string _zoneName = "Coordinated Universal Time";
    private bool _zoneFromBrowser;
    private bool _formatPending;
    private bool _formatInFlight;
    private int _formatVersion;

    [Inject] private IMessageTimeFormatter MessageTimeFormatter { get; set; } = null!;

    private string HeaderLabel => _presentation.HeaderLabel;

    private string HeaderMeta => _presentation.HeaderMeta ?? ClockZoneLabel;

    private string HeaderMetaCompact => CompactZoneLabel(HeaderMeta);

    // The mobile header has one short line for the zone, so an IANA path keeps only
    // its recognizable tail; the full value stays in the readout's accessible text.
    internal static string CompactZoneLabel(string label)
    {
        var slash = label.LastIndexOf('/');
        return slash >= 0 ? label[(slash + 1)..].Replace('_', ' ') : label;
    }

    private string ClockZoneLabel =>
        _presentation.IsLocal && !_zoneFromBrowser
            ? "Pending"
            : _zoneLabel;

    private string HeaderMetaTitle => _presentation.HeaderMetaTitle
        ?? (_presentation.IsLocal && !_zoneFromBrowser
            ? "Waiting for this device's time zone"
            : _zoneName);

    private string TimeColumnClass
    {
        get
        {
            if (!_presentation.IsClock)
                return "payload-message-list--durations";
            return _presentation.Is12Hour ? "payload-message-list--clock-12h" : "";
        }
    }

    private void InitializeMessageTime()
    {
        _preferences = MessageTimePreferences.Normalize(UiSettings.Ui.MessageTime);
        _presentation = MessageTimeDisplay.Describe(_preferences);
        UiSettings.UiPreferencesChanged += OnUiPreferencesChanged;
    }

    private MessageTimeFormatItem? ClockItem(MqttMessage message)
    {
        if (_clockItems.TryGetValue(message.Id, out var item))
            return item;

        return _presentation.IsLocal ? null : MessageTimeDisplay.FallbackClock(message, _preferences);
    }

    private MessageTimeFormatItem? DetailItem(MqttMessage message)
    {
        var item = ClockItem(message);
        if (item is null)
            return null;

        if (!string.IsNullOrEmpty(item.ZoneName) && !string.IsNullOrEmpty(item.OffsetLabel))
            return item;

        return _presentation.IsLocal ? null : MessageTimeDisplay.FallbackClock(message, _preferences);
    }

    private bool ClockIsPending(MqttMessage message) => _presentation.IsClock && ClockItem(message) is null;

    private string RowText(MqttMessage message)
    {
        if (!_presentation.IsClock)
            return MessageTimeDisplay.FormatDuration(_presentation.DurationFor(message));

        return ClockItem(message)?.TimeText ?? MessageTimeDisplay.PendingMarker;
    }

    private string RowTitle(MqttMessage message)
    {
        if (!_presentation.IsClock)
            return _presentation.DurationTitle(_presentation.DurationFor(message));

        return ClockItem(message)?.FullText ?? MessageTimeDisplay.PendingTitle;
    }

    private async Task FormatMessagesIfNeededAsync()
    {
        if (_formatInFlight || Disposed)
            return;

        _formatInFlight = true;
        try
        {
            while (!Disposed && _formatPending)
            {
                _formatPending = false;
                var preferences = _preferences;
                var version = _formatVersion;
                var request = BuildFormatRequest();
                if (request.Count == 0)
                    break;

                var result = await FormatBatchAsync(request, preferences);

                if (Disposed)
                    return;
                if (preferences != _preferences)
                {
                    _formatPending = true;
                    continue;
                }

                if (version != _formatVersion)
                    continue;

                if (result?.Items is not { } items)
                    break;

                if (!string.IsNullOrWhiteSpace(result.ZoneLabel))
                {
                    _zoneLabel = result.ZoneLabel;
                    _zoneFromBrowser = true;
                }

                if (!string.IsNullOrWhiteSpace(result.ZoneName))
                    _zoneName = result.ZoneName;

                ApplyFormatResult(items, request);
            }
        }
        finally
        {
            _formatInFlight = false;
        }

        if (!Disposed && _formatPending)
            StateHasChanged();
    }

    private async Task<MessageTimeFormatResult?> FormatBatchAsync(
        List<MessageTimeFormatRequest> request,
        MessageTimePreferences preferences)
    {
        try
        {
            return await MessageTimeFormatter.FormatAsync(request, preferences);
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Message time formatting failed");
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void ApplyFormatResult(List<MessageTimeFormatItem> items, List<MessageTimeFormatRequest> request)
    {
        var requestedEpochs = new Dictionary<Guid, long>(request.Count);
        foreach (var item in request)
            requestedEpochs[item.Key] = item.EpochMs;

        var currentEpochs = new Dictionary<Guid, long>(_messageList.Count + 1);
        foreach (var message in _messageList)
            currentEpochs[message.Id] = MessageTimeDisplay.EpochMillis(message.DateTimeReceived);
        if (_selectedMessage is { } selected)
            currentEpochs[selected.Id] = MessageTimeDisplay.EpochMillis(selected.DateTimeReceived);

        foreach (var item in items)
        {
            if (!currentEpochs.TryGetValue(item.Key, out var currentEpoch))
                continue;
            if (requestedEpochs.TryGetValue(item.Key, out var requestedEpoch) && requestedEpoch != currentEpoch)
                continue;

            _clockItems[item.Key] = item;
        }

        foreach (var key in _clockItems.Keys.Where(key => !currentEpochs.ContainsKey(key)).ToList())
            _clockItems.Remove(key);

        StateHasChanged();
    }

    private List<MessageTimeFormatRequest> BuildFormatRequest()
    {
        var request = new List<MessageTimeFormatRequest>(_presentation.IsClock ? _messageList.Count + 1 : 1);
        var selected = _selectedMessage;

        if (_presentation.IsClock)
        {
            foreach (var message in _messageList)
                request.Add(RequestFor(message, selected?.Id == message.Id));
        }

        var selectedIndex = selected is null ? -1 : request.FindIndex(item => item.Key == selected.Id);
        if (selected is null)
            return request;

        if (selectedIndex < 0)
            request.Add(RequestFor(selected, detail: true));
        else if (!request[selectedIndex].Detail)
            request[selectedIndex] = request[selectedIndex] with { Detail = true };

        return request;
    }

    private static MessageTimeFormatRequest RequestFor(MqttMessage message, bool detail) =>
        new(message.Id, MessageTimeDisplay.EpochMillis(message.DateTimeReceived), detail);

    private void OnUiPreferencesChanged() => _ = ApplyPreferencesAsync();

    private async Task ApplyPreferencesAsync()
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (Disposed)
                    return;

                var next = MessageTimePreferences.Normalize(UiSettings.Ui.MessageTime);
                if (next == _preferences)
                    return;

                if (next.Zone != _preferences.Zone)
                {
                    _zoneLabel = "UTC";
                    _zoneName = "Coordinated Universal Time";
                    _zoneFromBrowser = false;
                    _clockItems.Clear();
                }

                _formatVersion++;
                _preferences = next;
                _presentation = MessageTimeDisplay.Describe(next);
                _formatPending = true;
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) when (Disposed)
        {
            // The circuit was torn down before the preference was applied.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "PayloadBrowser time preference refresh failed");
        }
    }
}
