using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MqttProbe.UI.Components.Layout;
using MudBlazor;
using ChartSeriesModel = MqttProbe.Core.Models.Chart.ChartSeries;

namespace MqttProbe.UI.Components.Charts;

public partial class ChartCard
{
    private const string ExportUnavailableMessage =
        "The chart could not be prepared for export. Try again once it is drawing.";

    [Inject] private IDialogService DialogService { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;

    private ChartExportInterop? _exportInterop;
    private string? _capturedExportId;
    private ChartExportInterop? _capturingInterop;
    private IDialogReference? _exportDialog;
    private bool _exportBusy;

    private ChartExportInterop ExportInterop => _exportInterop ??= new ChartExportInterop(JS);

    private bool CanOpenExport => _echartsReady && !_exportBusy;

    private string ExportTitle
    {
        get
        {
            if (_echartsError is not null)
                return "Export unavailable because the chart could not start";

            return _echartsReady
                ? "Export chart data or image"
                : "Export chart (waiting for the chart to draw)";
        }
    }

    // The card sits at its file-length limit, so its export control is composed here.
    private RenderFragment ExportButton => builder =>
    {
        if (Configuration.Series.Count == 0)
            return;

        builder.OpenComponent<MudIconButton>(0);
        builder.AddAttribute(1, nameof(MudIconButton.Icon), LucideIcons.Download);
        builder.AddAttribute(2, nameof(MudIconButton.Color), MudBlazor.Color.Inherit);
        builder.AddAttribute(3, nameof(MudIconButton.Size), MudBlazor.Size.Small);
        builder.AddAttribute(4, nameof(MudIconButton.Disabled), !CanOpenExport);
        builder.AddAttribute(5, "title", ExportTitle);
        builder.AddAttribute(6, "aria-label", ExportTitle);
        builder.AddAttribute(7, nameof(MudIconButton.OnClick),
            EventCallback.Factory.Create<MouseEventArgs>(this, _ => OpenExportAsync()));
        builder.CloseComponent();
    };

    private async Task OpenExportAsync()
    {
        if (_disposed || !CanOpenExport)
            return;

        _exportBusy = true;
        StateHasChanged();

        var generation = _rendererGeneration;
        var interop = ExportInterop;

        try
        {
            var snapshot = await interop.CaptureAsync(_rendererChartId);
            if (!IsExportTargetCurrent(generation))
            {
                if (snapshot is not null)
                    await ReleaseSnapshotQuietlyAsync(interop, snapshot.Id);

                return;
            }

            if (snapshot is null)
            {
                ReportExportFailure();
                return;
            }

            _capturedExportId = snapshot.Id;
            _capturingInterop = interop;
            await ShowExportDialogAsync(interop, snapshot, generation);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or ObjectDisposedException)
        {
            _ = ex;
            if (!_disposed && IsExportTargetCurrent(generation))
                ReportExportFailure();
        }
        finally
        {
            _exportBusy = false;
            await ReleaseCapturedExportAsync();
            if (!_disposed)
                StateHasChanged();
        }
    }

    private async Task ShowExportDialogAsync(
        ChartExportInterop interop,
        ChartExportSnapshot snapshot,
        int generation)
    {
        if (!IsExportTargetCurrent(generation))
            return;

        var parameters = new DialogParameters<ChartExportDialog>
        {
            { dialog => dialog.ChartName, Configuration.Name },
            { dialog => dialog.Clock, Clock },
            { dialog => dialog.Series, BuildExportOptions(snapshot) },
            {
                dialog => dialog.Export,
                request => ExportCapturedAsync(interop, snapshot.Id, generation, request)
            },
        };
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, BackdropClick = false };
        var dialog = await DialogService.ShowAsync<ChartExportDialog>("Export chart", parameters, options);
        _exportDialog = dialog;

        if (!IsExportCurrent(interop, snapshot.Id, generation))
            CloseExportDialog();

        await dialog.Result;

        if (ReferenceEquals(_exportDialog, dialog))
            _exportDialog = null;
    }

    private async Task ExportCapturedAsync(
        ChartExportInterop interop,
        string snapshotId,
        int generation,
        ChartExportDialog.ExportRequest request)
    {
        if (!IsExportCurrent(interop, snapshotId, generation))
            throw new InvalidOperationException("The chart is no longer available for export.");

        await interop.ExportAsync(snapshotId, request.SeriesIds, request.Format, request.FileName);
    }

    private bool IsExportTargetCurrent(int generation) =>
        !_disposed
        && _echartsReady
        && generation == _rendererGeneration;

    private bool IsExportCurrent(ChartExportInterop interop, string snapshotId, int generation) =>
        IsExportTargetCurrent(generation)
        && ReferenceEquals(_capturingInterop, interop)
        && _capturedExportId == snapshotId;

    private void CloseExportDialog()
    {
        var dialog = Interlocked.Exchange(ref _exportDialog, null);
        if (dialog is null)
            return;

        try
        {
            dialog.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _ = ex;
        }
    }

    private List<ChartExportDialog.SeriesOption> BuildExportOptions(ChartExportSnapshot snapshot)
    {
        var configured = Configuration.Series
            .ToDictionary(series => series.Id.ToString(), StringComparer.OrdinalIgnoreCase);

        return [.. snapshot.Series.Select(summary => new ChartExportDialog.SeriesOption
        {
            Id = summary.Id,
            Name = summary.Name,
            Color = summary.Color,
            PointCount = summary.PointCount,
            Hint = configured.TryGetValue(summary.Id, out var series) ? SourceHint(series) : null,
        })];
    }

    private static string? SourceHint(ChartSeriesModel series)
    {
        var hasTopic = !string.IsNullOrWhiteSpace(series.Topic);
        var hasPath = !string.IsNullOrWhiteSpace(series.JsonPath);
        if (!hasTopic && !hasPath) return null;
        if (!hasPath) return series.Topic;
        if (!hasTopic) return series.JsonPath;

        return $"{series.Topic} › {series.JsonPath}";
    }

    private void ReportExportFailure() => Snackbar.Add(ExportUnavailableMessage, Severity.Error);

    private async Task ReleaseCapturedExportAsync()
    {
        var snapshotId = _capturedExportId;
        var interop = _capturingInterop;
        _capturedExportId = null;
        _capturingInterop = null;
        if (snapshotId is null || interop is null)
            return;

        await ReleaseSnapshotQuietlyAsync(interop, snapshotId);
    }

    private static async Task ReleaseSnapshotQuietlyAsync(ChartExportInterop interop, string snapshotId)
    {
        try
        {
            await interop.ReleaseAsync(snapshotId);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or ObjectDisposedException)
        {
            _ = ex;
        }
    }

    private async Task DisposeExportAsync()
    {
        CloseExportDialog();
        await ReleaseCapturedExportAsync();

        var interop = _exportInterop;
        _exportInterop = null;
        if (interop is not null)
            await interop.DisposeAsync();
    }
}
