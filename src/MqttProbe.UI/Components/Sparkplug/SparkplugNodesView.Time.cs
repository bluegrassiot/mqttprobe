using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Components.Sparkplug;

public partial class SparkplugNodesView
{
    private readonly Dictionary<string, Guid> _timeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceTimeFormatItem> _timeItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long EpochMs, bool Detail)> _timeFormatted = new(StringComparer.Ordinal);
    private MessageTimePreferences _timePreferences = new();
    private string _browserZoneLabel = "";
    private volatile bool _timePending;
    private bool _timeFormatInFlight;

    private SparkplugTimeDisplay TimeDisplay => new(_browserZoneLabel, _timeItems);

    private void InitializeTimeDisplay()
    {
        _timePreferences = MessageTimePreferences.Normalize(UiSettings.Ui.MessageTime);
        UiSettings.UiPreferencesChanged += OnUiTimePreferencesChanged;
        _timePending = true;
    }

    private void DisposeTimeDisplay() => UiSettings.UiPreferencesChanged -= OnUiTimePreferencesChanged;

    private void OnUiTimePreferencesChanged() => _ = ApplyTimePreferencesAsync();

    private async Task ApplyTimePreferencesAsync()
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (_disposed)
                    return;

                ApplyLatestTimePreferences();
                _timePending = true;
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // The circuit was torn down before the preference was applied.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Sparkplug nodes time preference refresh failed");
        }
    }

    private void ApplyLatestTimePreferences()
    {
        var next = MessageTimePreferences.Normalize(UiSettings.Ui.MessageTime);
        if (next == _timePreferences)
            return;

        if (next.Zone != _timePreferences.Zone)
        {
            _browserZoneLabel = "";
            _timeItems.Clear();
        }

        _timeFormatted.Clear();
        _timePreferences = next;
    }

    private void MarkTimePending() => _timePending = true;

    internal async Task FormatTimesIfNeededAsync()
    {
        if (_disposed || _timeFormatInFlight)
            return;

        _timeFormatInFlight = true;
        try
        {
            while (!_disposed && _timePending)
            {
                _timePending = false;
                ApplyLatestTimePreferences();
                var preferences = _timePreferences;
                var requests = BuildTimeRequests();
                if (requests.Count == 0)
                    break;

                var result = await FormatTimeBatchAsync(requests, preferences);

                if (_disposed)
                    return;

                if (preferences != _timePreferences)
                {
                    _timePending = true;
                    continue;
                }

                if (result is null)
                    break;

                ApplyTimeResult(requests, result);
                StateHasChanged();
            }
        }
        finally
        {
            _timeFormatInFlight = false;
        }

        if (!_disposed && _timePending)
            StateHasChanged();
    }

    private async Task<DeviceTimeFormatResult?> FormatTimeBatchAsync(
        List<TimeRequest> requests,
        MessageTimePreferences preferences)
    {
        try
        {
            return await TimeFormatter.FormatAsync(requests.Select(request => request.Request).ToList(), preferences);
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Sparkplug nodes time formatting failed");
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private List<TimeRequest> BuildTimeRequests()
    {
        var requests = new List<TimeRequest>();
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var instants = new List<TimeInstant>();

        if (_selectedNode is { } node)
        {
            AddInstant(instants, SparkplugTimeKeys.NodeBirth, node.LastBirthAt, detail: true);
            AddInstant(instants, SparkplugTimeKeys.NodeLastData, node.LastDataAt, detail: true);
            AddInstant(instants, SparkplugTimeKeys.NodeDeath, node.LastDeathAt, detail: true);
            AddInstant(instants, SparkplugTimeKeys.NodeRebirthRequest, node.LastRebirthRequestAt, detail: true);

            var nodeTopic = SparkplugChartTargets.NodeTopic(node.GroupId, node.NodeId);
            foreach (var metric in node.Metrics)
                AddInstant(instants, SparkplugTimeKeys.Metric(nodeTopic, metric.Name), metric.LastUpdated, detail: false);

            if (_selectedDevice is { } device)
            {
                var deviceTopic = SparkplugChartTargets.DeviceTopic(node.GroupId, node.NodeId, device.DeviceId);
                foreach (var metric in device.Metrics)
                    AddInstant(instants, SparkplugTimeKeys.Metric(deviceTopic, metric.Name), metric.LastUpdated, detail: false);
            }
        }

        foreach (var instant in instants)
            AddTimeRequest(requests, needed, instant.Label, instant.EpochMs, instant.Detail);

        if (instants.Count > 0)
        {
            var newestEpoch = instants.Max(instant => instant.EpochMs);
            AddTimeRequest(requests, needed, SparkplugTimeKeys.RegionCaption, newestEpoch, detail: true);
        }

        PruneTimeItems(needed);
        return requests;
    }

    private static void AddInstant(List<TimeInstant> instants, string label, DateTime? instant, bool detail)
    {
        if (instant is { } value)
            instants.Add(new TimeInstant(label, ToEpochMillis(value), detail));
    }

    private void AddTimeRequest(
        List<TimeRequest> requests,
        HashSet<string> needed,
        string label,
        long epochMs,
        bool detail)
    {
        needed.Add(label);

        if (_timeFormatted.TryGetValue(label, out var cached)
            && cached.EpochMs == epochMs
            && cached.Detail == detail)
        {
            return;
        }

        if (!_timeKeys.TryGetValue(label, out var key))
        {
            key = Guid.NewGuid();
            _timeKeys[label] = key;
        }

        requests.Add(new TimeRequest(label, new DeviceTimeFormatRequest(key, epochMs, detail)));
    }

    private void PruneTimeItems(HashSet<string> needed)
    {
        if (_timeItems.Count == needed.Count && _timeItems.Keys.All(needed.Contains))
            return;

        foreach (var label in _timeItems.Keys.Where(label => !needed.Contains(label)).ToList())
            _timeItems.Remove(label);
        foreach (var label in _timeFormatted.Keys.Where(label => !needed.Contains(label)).ToList())
            _timeFormatted.Remove(label);
        foreach (var label in _timeKeys.Keys.Where(label => !needed.Contains(label)).ToList())
            _timeKeys.Remove(label);
    }

    private void ApplyTimeResult(List<TimeRequest> requests, DeviceTimeFormatResult result)
    {
        if (result.Items is { } items)
        {
            var byKey = items.ToDictionary(item => item.Key);
            foreach (var request in requests)
            {
                if (!byKey.TryGetValue(request.Request.Key, out var item))
                    continue;

                _timeItems[request.Label] = item;
                _timeFormatted[request.Label] = (request.Request.EpochMs, request.Request.Detail);
            }
        }

        if (!string.IsNullOrWhiteSpace(result.ZoneLabel))
            _browserZoneLabel = result.ZoneLabel;
    }

    private static long ToEpochMillis(DateTime instant) =>
        new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private readonly record struct TimeInstant(string Label, long EpochMs, bool Detail);

    private readonly record struct TimeRequest(string Label, DeviceTimeFormatRequest Request);
}
