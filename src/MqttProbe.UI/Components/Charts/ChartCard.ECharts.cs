using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;

namespace MqttProbe.UI.Components.Charts;

public partial class ChartCard
{
    private const string EChartsUnavailableMessage =
        "ECharts could not start, so this chart is not drawing. Switch back to Apex to keep using it.";

    private readonly string _rendererChartId = Guid.NewGuid().ToString("N");
    private ChartRendererInterop? _rendererInterop;
    private DotNetObjectReference<ChartCard>? _rendererOwner;
    private ElementReference _echartsElement;
    private ChartRendererKind _activeRenderer = ChartRendererKind.Apex;
    private bool _echartsReady;
    private bool _echartsStarting;
    private bool _echartsLive;
    private bool _echartsApplyActive;
    private bool _pendingViewportReset;
    private string? _echartsError;
    private string? _echartsErrorDetail;
    private int _rendererGeneration;
    private int _perfSequence;
    private ChartPerformanceProbe.StorageSampler? _storageSampler;
    private int _storageSamplerGeneration = -1;
    private Guid[] _storageSamplerSeries = [];

    private string PauseTitle => Renderer == ChartRendererKind.ECharts
        ? "Pause live updates (keeps the view still)"
        : "Pause live updates (enables zoom/pan/export)";

    private void SwitchRenderer()
    {
        _activeRenderer = Renderer;
        _rendererGeneration++;
        _echartsReady = false;
        _echartsError = null;
        _echartsErrorDetail = null;
        _visibleFromMs = null;
        _visibleToMs = null;
        _zoneCaptionSignature = "";
        _hovering = false;
        _pendingUpdate = true;
        _pendingViewportReset = false;

        _ = InvokeAsync(DisposeChartQuietlyAsync);

        if (Renderer == ChartRendererKind.Apex)
            _pendingOptionsUpdate = true;
        else if (_bridgeReady)
            _ = InvokeAsync(() => RefreshZoneCaptionAsync(force: true));
    }

    private async Task EnsureEChartsAsync()
    {
        if (_disposed || _echartsReady || _echartsStarting || _echartsError is not null
            || _echartsElement.Context is null)
        {
            return;
        }

        var generation = _rendererGeneration;
        var specStart = ChartPerformanceProbe.Mark();
        var spec = BuildEChartsSpec();
        var perfToken = ChartPerformanceProbe.SpecPrepared(
            _rendererChartId,
            generation,
            NextPerfSequence(),
            ChartPerformanceProbe.Create,
            specStart,
            spec,
            new ChartPerformanceProbe.SpecState(
                _live, _echartsReady, _pendingUpdate || _pendingOptionsUpdate, _pendingViewportReset));
        _pendingUpdate = false;
        _pendingOptionsUpdate = false;
        _pendingViewportReset = false;
        _echartsStarting = true;
        bool created;
        JSException? createFailure = null;
        try
        {
            _rendererOwner ??= DotNetObjectReference.Create(this);
            created = await RendererInterop.CreateAsync(
                _echartsElement, _rendererChartId, _rendererOwner, spec, perfToken);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser drops the chart with the page.
            _echartsReady = false;
            return;
        }
        catch (JSException ex)
        {
            created = false;
            createFailure = ex;
        }
        finally
        {
            _echartsStarting = false;
        }

        if (createFailure is not null)
            HandleEChartsCreateFailure(createFailure, generation);
        else
            await CompleteEChartsCreationAsync(created, generation);
    }

    private async Task CompleteEChartsCreationAsync(bool created, int generation)
    {
        if (_disposed || generation != _rendererGeneration)
        {
            if (created)
            {
                _echartsLive = true;
                await DisposeChartQuietlyAsync();
            }

            if (!_disposed)
                StateHasChanged();
            return;
        }

        if (!created)
        {
            FailECharts(failure: null);
            return;
        }

        _echartsLive = true;
        _echartsReady = true;
        EnsureStorageSampler(generation);
        if (_live && (_pendingUpdate || _pendingOptionsUpdate))
            await ApplyEChartsDataAsync();

        if (!_disposed)
            StateHasChanged();
    }

    private void HandleEChartsCreateFailure(JSException failure, int generation)
    {
        if (_disposed)
            return;

        if (generation == _rendererGeneration)
            FailECharts(failure);
        else if (Renderer == ChartRendererKind.ECharts)
            StateHasChanged();
    }

    private async Task ApplyEChartsDataAsync()
    {
        if (_disposed || !_echartsReady || !_live)
            return;

        // One apply owns this card's interop slot. Requests that arrive while it waits only leave
        // the pending flags set, so the owner draws them from the newest buffer after it returns
        // instead of queueing another snapshot behind it.
        if (_echartsApplyActive)
            return;

        _echartsApplyActive = true;
        try
        {
            while (!_disposed && _live && _echartsReady && (_pendingUpdate || _pendingOptionsUpdate))
            {
                if (!await ApplyEChartsSnapshotAsync())
                    return;
            }
        }
        finally
        {
            _echartsApplyActive = false;
        }
    }

    private async Task<bool> ApplyEChartsSnapshotAsync()
    {
        var generation = _rendererGeneration;
        EnsureStorageSampler(generation);
        var resetViewport = _pendingViewportReset;
        var specStart = ChartPerformanceProbe.Mark();
        var spec = BuildEChartsSpec();
        var perfToken = ChartPerformanceProbe.SpecPrepared(
            _rendererChartId,
            generation,
            NextPerfSequence(),
            ChartPerformanceProbe.Update,
            specStart,
            spec,
            new ChartPerformanceProbe.SpecState(
                _live, _echartsReady, true, resetViewport));
        _pendingUpdate = false;
        _pendingOptionsUpdate = false;
        _pendingViewportReset = false;

        bool applied;
        try
        {
            applied = await RendererInterop.UpdateAsync(_rendererChartId, spec, resetViewport, perfToken);
        }
        catch (JSDisconnectedException)
        {
            _echartsReady = false;
            return false;
        }
        catch (JSException ex)
        {
            if (!_disposed && generation == _rendererGeneration)
                FailECharts(ex);
            return false;
        }

        if (_disposed || generation != _rendererGeneration)
            return false;

        if (!applied)
        {
            FailECharts(failure: null);
            return false;
        }

        return true;
    }

    private int NextPerfSequence() => ChartPerformanceProbe.Enabled ? ++_perfSequence : 0;

    private void EnsureStorageSampler(int generation)
    {
        if (!ChartPerformanceProbe.Enabled)
        {
            if (_storageSampler is not null)
                StopStorageSampler();
            return;
        }

        var seriesIds = Configuration.Series.Select(series => series.Id).ToArray();
        if (_storageSampler is not null
            && generation == _storageSamplerGeneration
            && seriesIds.SequenceEqual(_storageSamplerSeries))
        {
            return;
        }

        StopStorageSampler();
        _storageSamplerGeneration = generation;
        _storageSamplerSeries = seriesIds;
        _storageSampler = ChartPerformanceProbe.StorageSampler.Start(
            _rendererChartId,
            generation,
            seriesIds,
            ChartDataService.GetLastPointTimestamp);
    }

    private void StopStorageSampler()
    {
        _storageSampler?.Dispose();
        _storageSampler = null;
        _storageSamplerGeneration = -1;
        _storageSamplerSeries = [];
    }

    private void FailECharts(Exception? failure)
    {
        _echartsReady = false;
        _echartsError = EChartsUnavailableMessage;
        _echartsErrorDetail = failure?.Message;
        _ = InvokeAsync(DisposeChartQuietlyAsync);
        if (!_disposed)
            StateHasChanged();
    }

    // The plot markup disappears with the last series, so the old instance must not survive it.
    private async Task ReleaseEChartsPlotAsync()
    {
        StopStorageSampler();
        if (!_echartsReady && !_echartsLive && !_echartsStarting)
            return;

        _rendererGeneration++;
        await DisposeChartQuietlyAsync();
    }

    private async Task DisposeChartQuietlyAsync()
    {
        StopStorageSampler();
        CloseExportDialog();
        _echartsReady = false;
        if (_rendererInterop is null || (!_echartsLive && !_echartsStarting))
            return;

        _echartsLive = false;
        try
        {
            await _rendererInterop.DisposeChartAsync(_rendererChartId);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or ObjectDisposedException)
        {
            // The chart is already unreachable; nothing is left to release in this page.
            _ = ex;
        }
    }

    private async Task DisposeRendererAsync()
    {
        await DisposeExportAsync();
        await DisposeChartQuietlyAsync();

        _rendererOwner?.Dispose();
        _rendererOwner = null;

        if (_rendererInterop is not null)
        {
            await _rendererInterop.DisposeAsync();
            _rendererInterop = null;
        }
    }

    private ChartRendererInterop RendererInterop => _rendererInterop ??= new ChartRendererInterop(JS);

    private ChartRendererSpec BuildEChartsSpec()
    {
        var (zone, clockFormat) = ChartTimeFormat.Values(_preferences);

        return new ChartRendererSpec(
            Type: Configuration.Type switch
            {
                ChartType.Area => "area",
                ChartType.Bar => "bar",
                _ => "line",
            },
            Dark: IsDarkMode,
            Zone: zone,
            ClockFormat: clockFormat,
            FontProfile: FontProfiles.Normalize(UiSettings.Ui.FontProfile),
            Series: [.. Configuration.Series.Select(series => new ChartRendererSeries(
                series.Id.ToString(),
                series.DisplayName,
                series.Color,
                [.. GetPoints(series.Id).Select(point =>
                    new[] { (double)Epoch(point.Timestamp), point.Value })]))]);
    }

    private Task RequestRendererRollback() => OnRendererRollback.InvokeAsync();

    [JSInvokable]
    public async Task OnEChartsViewportChanged(double? fromMs, double? toMs)
    {
        if (_disposed || Renderer != ChartRendererKind.ECharts)
            return;

        if (fromMs is { } from && toMs is { } to)
        {
            _visibleFromMs = (long)Math.Round(from);
            _visibleToMs = (long)Math.Round(to);
        }
        else
        {
            _visibleFromMs = null;
            _visibleToMs = null;
        }

        _zoneCaptionSignature = "";
        await RefreshZoneCaptionAsync(force: true);
    }
}
