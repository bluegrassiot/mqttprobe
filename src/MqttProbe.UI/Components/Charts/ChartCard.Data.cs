namespace MqttProbe.UI.Components.Charts;

public partial class ChartCard
{
    private int _dataUpdateQueued;

    // Receipts come from the newest buffered point, which the display window never filters.
    private void RefreshReceipts()
    {
        _lastReceipts.Clear();
        foreach (var seriesId in Configuration.Series.Select(series => series.Id))
            _lastReceipts[seriesId] = ChartDataService.GetLastPointTimestamp(seriesId);
    }

    private void OnDataUpdated()
    {
        if (Interlocked.Exchange(ref _dataUpdateQueued, 1) == 1)
            return;

        _ = InvokeAsync(ProcessDataUpdate);
    }

    private void ProcessDataUpdate()
    {
        // Cleared before reading receipts so a message arriving mid-pass queues another one.
        Interlocked.Exchange(ref _dataUpdateQueued, 0);
        if (_disposed)
            return;

        _hasNewData = true;
        RefreshReceipts();
        RefreshFreshness(render: true);
        RefreshChart();

        if (_live && !(HoverPausesUpdates && _hovering) && _visibleFromMs is null)
            _ = InvokeAsync(() => RefreshZoneCaptionAsync());
    }
}
