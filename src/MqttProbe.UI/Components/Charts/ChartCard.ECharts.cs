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
    private bool _pendingViewportReset;
    private string? _echartsError;
    private string? _echartsErrorDetail;
    private int _rendererGeneration;

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
        var spec = BuildEChartsSpec();
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
                _echartsElement, _rendererChartId, _rendererOwner, spec);
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
        if (_live && (_pendingUpdate || _pendingOptionsUpdate))
            await ApplyEChartsDataAsync();
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
        if (_disposed || !_echartsReady)
            return;

        var spec = BuildEChartsSpec();
        var resetViewport = _pendingViewportReset;
        _pendingUpdate = false;
        _pendingOptionsUpdate = false;
        _pendingViewportReset = false;

        try
        {
            if (!await RendererInterop.UpdateAsync(_rendererChartId, spec, resetViewport))
                FailECharts(failure: null);
        }
        catch (JSDisconnectedException)
        {
            _echartsReady = false;
        }
        catch (JSException ex)
        {
            if (!_disposed)
                FailECharts(ex);
        }
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
        if (!_echartsReady && !_echartsLive && !_echartsStarting)
            return;

        _rendererGeneration++;
        await DisposeChartQuietlyAsync();
    }

    private async Task DisposeChartQuietlyAsync()
    {
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
