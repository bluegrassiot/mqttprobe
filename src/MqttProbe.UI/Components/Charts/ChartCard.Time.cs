using ApexCharts;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Components.Charts;

public partial class ChartCard
{
    private static readonly TimeSpan _captionRequestInterval = TimeSpan.FromMilliseconds(400);
    private const int MaxCaptionAttempts = 5;

    private MessageTimePreferences _preferences = new();
    private bool _bridgeReady;
    private int _bridgeAttempts;
    private int _captionAttempts;
    private string _zoneCaption = "";
    private string _zoneDetails = "";
    private long? _visibleFromMs;
    private long? _visibleToMs;
    private string _zoneCaptionSignature = "";
    private string _desiredCaptionSignature = "";
    private string? _captionInFlightSignature;
    private int _captionInFlightVersion;
    private int _captionVersion;
    private bool _captionPending;
    private DateTime _lastCaptionRequest = DateTime.MinValue;

    private string ZoneAccessibleLabel => $"{_zoneCaption}: {_zoneDetails}";

    private void InitializeTimeDisplay()
    {
        _preferences = ReadPreferences();
        UiSettings.UiPreferencesChanged += OnUiPreferencesChanged;
    }

    private void DisposeTimeDisplay() => UiSettings.UiPreferencesChanged -= OnUiPreferencesChanged;

    private MessageTimePreferences ReadPreferences() =>
        MessageTimePreferences.Normalize(UiSettings.Ui.MessageTime);

    private void ApplyTimeFormatPreferences()
    {
        _options.Xaxis.Labels.DatetimeUTC = _preferences.Zone == MessageTimePreferences.ZoneUtc;
        _options.Xaxis.Labels.Formatter = ChartTimeFormat.AxisFormatter(_preferences);
        _options.Tooltip.X.Formatter = ChartTimeFormat.TooltipFormatter(_preferences);
    }

    internal async Task EnsureTimeBridgeAsync()
    {
        if (_disposed || _bridgeReady || _bridgeAttempts >= 5)
            return;

        _bridgeAttempts++;
        bool ready;
        try
        {
            ready = await TimeFormatter.EnsureBridgeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or ObjectDisposedException)
        {
            ready = false;
        }

        if (!ready || _disposed)
            return;

        _bridgeReady = true;
        await RefreshZoneCaptionAsync(force: true);
        if (!_disposed) StateHasChanged();
    }

    private async Task RefreshZoneCaptionAsync(bool force = false, bool retry = false)
    {
        if (_disposed || !_bridgeReady)
            return;

        var range = CurrentRange();
        if (range is null)
        {
            ClearZoneCaption();
            return;
        }

        var (from, to) = range.Value;
        var signature = CaptionSignature(from, to);
        var targetChanged = signature != _desiredCaptionSignature;
        _desiredCaptionSignature = signature;

        if (targetChanged)
        {
            _captionAttempts = 0;
            InvalidateCaptionRequest();
            _captionPending = false;
        }
        else if (_captionInFlightSignature == signature)
        {
            return;
        }

        if (signature == _zoneCaptionSignature)
        {
            _captionPending = false;
            return;
        }

        if (retry && _captionAttempts >= MaxCaptionAttempts)
        {
            _captionPending = false;
            return;
        }

        var now = Clock.GetUtcNow().UtcDateTime;
        if (!force && !retry && now - _lastCaptionRequest < _captionRequestInterval)
        {
            _captionPending = true;
            return;
        }

        if (!retry)
            _captionAttempts = 0;

        _lastCaptionRequest = now;
        _captionPending = false;
        await RequestCaptionAsync(from, to, signature);
    }

    private async Task RequestCaptionAsync(long from, long to, string signature)
    {
        var version = ++_captionVersion;
        _captionInFlightSignature = signature;
        _captionInFlightVersion = version;

        var preferences = _preferences;
        var zone = preferences.Zone;
        var requests = BuildCaptionRequests(from, to);

        DeviceTimeFormatResult? result;
        try
        {
            result = await TimeFormatter.FormatAsync(requests, preferences);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or ObjectDisposedException)
        {
            ClearCaptionInFlight(version);
            if (IsCurrent(version, zone))
                CaptionFailed();
            return;
        }

        ClearCaptionInFlight(version);
        if (!IsCurrent(version, zone))
            return;

        if (result is null)
        {
            CaptionFailed();
            return;
        }

        ApplyCaptionResult(result, signature);
    }

    private void InvalidateCaptionRequest()
    {
        _captionVersion++;
        _captionInFlightSignature = null;
        _captionInFlightVersion = 0;
    }

    private void ClearCaptionInFlight(int version)
    {
        if (_captionInFlightVersion != version)
            return;

        _captionInFlightSignature = null;
        _captionInFlightVersion = 0;
    }

    private bool IsCurrent(int version, string zone) =>
        !_disposed && version == _captionVersion && zone == _preferences.Zone;

    private void ApplyCaptionResult(DeviceTimeFormatResult result, string signature)
    {
        var description = ChartZoneCaption.Describe(result);
        _zoneCaptionSignature = signature;
        _captionAttempts = 0;
        _captionPending = false;
        _zoneCaption = description.Visible;
        _zoneDetails = description.Details;
        if (!_disposed) StateHasChanged();
    }

    private void CaptionFailed()
    {
        _captionAttempts++;
        _captionPending = _captionAttempts < MaxCaptionAttempts;
    }

    private void ClearZoneCaption()
    {
        _zoneCaptionSignature = "";
        _desiredCaptionSignature = "";
        _captionAttempts = 0;
        _captionPending = false;
        InvalidateCaptionRequest();
        if (_zoneCaption.Length == 0 && _zoneDetails.Length == 0)
            return;

        _zoneCaption = "";
        _zoneDetails = "";
        if (!_disposed) StateHasChanged();
    }

    private (long From, long To)? CurrentRange()
    {
        if (_visibleFromMs is { } zoomFrom && _visibleToMs is { } zoomTo)
            return zoomFrom <= zoomTo ? (zoomFrom, zoomTo) : (zoomTo, zoomFrom);

        var data = DataRange();
        if (data is not null)
            return data;

        var now = Epoch(Clock.GetUtcNow().UtcDateTime);
        return (now, now);
    }

    private (long From, long To)? DataRange()
    {
        long? min = null;
        long? max = null;

        foreach (var series in Configuration.Series)
        {
            var points = GetPoints(series.Id);
            if (points.Count == 0)
                continue;

            var first = Epoch(points[0].Timestamp);
            var last = Epoch(points[^1].Timestamp);
            var low = Math.Min(first, last);
            var high = Math.Max(first, last);
            min = min is null ? low : Math.Min(min.Value, low);
            max = max is null ? high : Math.Max(max.Value, high);
        }

        return min is null ? null : (min.Value, max!.Value);
    }

    private string CaptionSignature(long from, long to) =>
        $"{_preferences.Zone}|{from / 5000}|{to / 5000}";

    private static List<DeviceTimeFormatRequest> BuildCaptionRequests(long from, long to)
    {
        const int sampleCount = 5;
        var span = to - from;
        var seen = new HashSet<long>();
        var requests = new List<DeviceTimeFormatRequest>(sampleCount);

        for (var index = 0; index < sampleCount; index++)
        {
            var epoch = span == 0
                ? from
                : from + (long)Math.Round(span * (double)index / (sampleCount - 1));
            if (!seen.Add(epoch))
                continue;

            requests.Add(new DeviceTimeFormatRequest(Guid.NewGuid(), epoch, Detail: true));
        }

        return requests;
    }

    private static long Epoch(DateTime timestamp) =>
        new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private async Task OnChartZoomed(ZoomedData<ChartDataPoint> args)
    {
        if (args?.IsZoomed == true && args.XAxis?.Min is { } min && args.XAxis.Max is { } max)
        {
            _visibleFromMs = (long)Math.Round(min);
            _visibleToMs = (long)Math.Round(max);
        }
        else
        {
            _visibleFromMs = null;
            _visibleToMs = null;
        }

        _zoneCaptionSignature = "";
        await RefreshZoneCaptionAsync(force: true);
    }

    private async Task OnChartScrolled(SelectionData<ChartDataPoint> args)
    {
        if (args?.XAxis?.Min is { } min && args.XAxis.Max is { } max)
        {
            _visibleFromMs = (long)Math.Round(min);
            _visibleToMs = (long)Math.Round(max);
        }

        await RefreshZoneCaptionAsync();
    }

    private void OnUiPreferencesChanged() => _ = InvokeAsync(ApplyUiPreferencesAsync);

    private async Task ApplyUiPreferencesAsync()
    {
        if (_disposed)
            return;

        var next = ReadPreferences();
        if (next == _preferences)
            return;

        var zoneChanged = next.Zone != _preferences.Zone;
        _preferences = next;
        ApplyTimeFormatPreferences();
        _pendingOptionsUpdate = true;

        if (zoneChanged)
        {
            _zoneCaptionSignature = "";
            if (_bridgeReady)
                await RefreshZoneCaptionAsync(force: true);
        }

        if (!_disposed)
            StateHasChanged();
    }
}
