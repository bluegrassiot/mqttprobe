using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Chart;
using MqttProbe.UI.Services.Formatting;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using ChartCardComponent = MqttProbe.UI.Components.Charts.ChartCard;
using ChartExportDialogComponent = MqttProbe.UI.Components.Charts.ChartExportDialog;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartCardExportTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IChartDataService _chartData = null!;
    private ChartTimeFormatterStub _formatter = null!;
    private FakeTimeProvider _clock = null!;
    private ISnackbar _snackbar = null!;
    private ChartRuntime _runtime = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private readonly Dictionary<Guid, List<ChartDataPoint>> _points = [];

    [SetUp]
    public void SetupMocks()
    {
        _chartData = Substitute.For<IChartDataService>();
        _chartData.GetPoints(Arg.Any<Guid>())
            .Returns(call => (IReadOnlyList<ChartDataPoint>)PointsFor(call.Arg<Guid>()));
        _clock = new FakeTimeProvider(_start);
        _formatter = new ChartTimeFormatterStub();
        _snackbar = Substitute.For<ISnackbar>();
        _runtime = new ChartRuntime(JSInterop.JSRuntime);
        _runtime.Renderer.Handler = RendererCalls;
        _runtime.Export.Handler = ExportCalls;

        Services.AddSingleton(_chartData);
        Services.AddSingleton(SettingsSubstituteRegistration.CreateUiSettings());
        Services.AddSingleton<IDeviceTimeFormatter>(_formatter);
        Services.AddSingleton(_snackbar);
        Services.AddSingleton<IJSRuntime>(_runtime);
        EnsureMudProviders();

        _dialogProvider = Render<MudDialogProvider>();
    }

    private static Task<object?> RendererCalls(string identifier, object?[]? args) =>
        Task.FromResult<object?>(identifier switch
        {
            "createChart" or "updateChart" or "disposeChart" => true,
            _ => null,
        });

    private Task<object?> ExportCalls(string identifier, object?[]? args) =>
        Task.FromResult<object?>(identifier == "captureSnapshot" ? _runtime.Snapshot : null);

    private IReadOnlyList<ChartDataPoint> PointsFor(Guid seriesId) =>
        _points.TryGetValue(seriesId, out var points) ? [.. points] : [];

    private void Record(ChartSeries series, double secondsAgo, double value = 1)
    {
        if (!_points.TryGetValue(series.Id, out var points))
            _points[series.Id] = points = [];
        points.Add(new ChartDataPoint(_clock.GetUtcNow().UtcDateTime.AddSeconds(-secondsAgo), value));
    }

    private static ChartConfiguration Chart(params ChartSeries[] series) => new()
    {
        Name = "Chart",
        Series = [.. series]
    };

    private static ChartSeries Series(
        string name,
        string color = "#F97316",
        string topic = "",
        string path = "") => new()
        {
            DisplayName = name,
            Color = color,
            Topic = topic,
            JsonPath = path
        };

    private static ChartExportSnapshot Snapshot(string id, params ChartSeries[] series) =>
        new(id, MessageTimePreferences.ZoneUtc, MessageTimePreferences.ClockFormat24Hour,
            [.. series.Select(entry => new ChartExportSeriesSummary(
                entry.Id.ToString(), entry.DisplayName, entry.Color, 2))]);

    private IRenderedComponent<ChartCardComponent> RenderCard(
        ChartConfiguration configuration,
        bool allowExport = true) =>
        Render<ChartCardComponent>(parameters => parameters
            .Add(card => card.Configuration, configuration)
            .Add(card => card.Clock, _clock)
            .Add(card => card.AllowExport, allowExport));

    private void WaitForChart(IRenderedComponent<ChartCardComponent> cut) =>
        cut.WaitForAssertion(
            () => cut.FindAll("button[title='Export chart data or image']").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));

    private void WaitForDialog() =>
        _dialogProvider.WaitForAssertion(
            () => _dialogProvider.FindAll(".mud-dialog").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));

    private void ClickExport(IRenderedComponent<ChartCardComponent> cut) =>
        cut.Find("button[title='Export chart data or image']").Click();

    private void ClickDialogAction(string label) =>
        _dialogProvider.FindAll(".mud-dialog-actions button")
            .Single(button => button.TextContent.Trim() == label).Click();

    private bool DialogActionDisabled(string label) =>
        _dialogProvider.FindAll(".mud-dialog-actions button")
            .Single(button => button.TextContent.Trim() == label).HasAttribute("disabled");

    [Test]
    public void ExportControl_IsOfferedOnceTheChartDraws()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        var chart = Chart(series);

        var cut = RenderCard(chart);
        WaitForChart(cut);
        cut.Find("button[title='Export chart data or image']").HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public void ExportControl_IsAbsentWhenTheCardDisallowsExport()
    {
        var series = Series("Temp");
        var second = Series("Flow", "#A78BFA");
        Record(series, 2, 21.5);
        Record(second, 1, 3);

        var cut = RenderCard(Chart(series), allowExport: false);
        cut.WaitForAssertion(
            () => _runtime.Renderer.Invoked("createChart").Should().Be(1),
            TimeSpan.FromSeconds(5));

        // updateChart is only issued once the plot is ready, so it pins readiness without
        // relying on the control under test.
        cut.Render(parameters => parameters
            .Add(card => card.Configuration, Chart(series, second))
            .Add(card => card.Clock, _clock)
            .Add(card => card.AllowExport, false));
        cut.WaitForAssertion(
            () => _runtime.Renderer.Invoked("updateChart").Should().BeGreaterThan(0),
            TimeSpan.FromSeconds(5));

        cut.FindAll("button[title*='Export']").Should().BeEmpty(
            "a disallowed export removes the control instead of disabling it");
        cut.Markup.Should().NotContain("Export chart");
        cut.FindAll("button[title='Edit chart']").Should().ContainSingle();
        cut.FindAll("button[title='Delete chart']").Should().ContainSingle();
        _runtime.Export.Invoked("captureSnapshot").Should().Be(0);
    }

    [Test]
    public void ExportControl_IsRemovedWhenExportIsDisallowedAfterTheChartDraws()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        var chart = Chart(series);

        var cut = RenderCard(chart);
        WaitForChart(cut);

        cut.Render(parameters => parameters
            .Add(card => card.Configuration, chart)
            .Add(card => card.Clock, _clock)
            .Add(card => card.AllowExport, false));

        cut.FindAll("button[title*='Export']").Should().BeEmpty(
            "the ready control is removed, not left behind in a disabled state");
        cut.Markup.Should().NotContain("Export chart");
        _runtime.Export.Invoked("captureSnapshot").Should().Be(0);
    }

    [Test]
    public void RendererFailure_MarksExportUnavailable()
    {
        _runtime.Renderer.Handler = (identifier, _) => identifier == "createChart"
            ? Task.FromException<object?>(new JSException("Failed to fetch dynamically imported module"))
            : Task.FromResult<object?>(true);
        var series = Series("Temp");
        Record(series, 1, 21.5);
        var chart = Chart(series);

        var cut = RenderCard(chart);

        cut.WaitForAssertion(
            () => cut.FindAll(".chart-card-echarts-error").Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        cut.Find("button[title='Export unavailable because the chart could not start']")
            .HasAttribute("disabled").Should().BeTrue();
    }

    [Test]
    public async Task ExportControl_WaitsForTheChartToDraw()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtime.Renderer.Handler = async (identifier, _) =>
        {
            if (identifier == "createChart")
            {
                started.TrySetResult();
                await resume.Task;
            }

            return true;
        };
        var series = Series("Temp");
        Record(series, 1, 21.5);

        var cut = RenderCard(Chart(series));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cut.Find("button[title='Export chart (waiting for the chart to draw)']")
            .HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().NotContain("Export chart data or image");

        resume.SetResult();

        cut.WaitForAssertion(
            () => cut.Find("button[title='Export chart data or image']")
                .HasAttribute("disabled").Should().BeFalse(),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ExportCapture_HappensBeforeTheDialogOpens()
    {
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        _runtime.Export.Handler = async (identifier, _) =>
        {
            if (identifier != "captureSnapshot")
                return null;

            captureStarted.TrySetResult();
            await captureResume.Task;
            return _runtime.Snapshot;
        };

        var cut = RenderCard(Chart(series));
        WaitForChart(cut);
        ClickExport(cut);
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(
            "the snapshot is captured before the series list is shown");

        captureResume.SetResult();

        WaitForDialog();
        var chartId = _runtime.Renderer.ArgsFor("createChart")![1];
        _runtime.Export.ArgsFor("captureSnapshot")![0].Should().Be(chartId,
            "the capture targets the card's own chart instance");
    }

    [Test]
    public void CancellingTheDialog_ReleasesTheCapture()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        var cut = RenderCard(Chart(series));
        WaitForChart(cut);

        ClickExport(cut);
        WaitForDialog();
        ClickDialogAction("Cancel");

        cut.WaitForAssertion(
            () => _runtime.Export.Invoked("releaseSnapshot").Should().Be(1),
            TimeSpan.FromSeconds(5));
        _runtime.Export.ArgsFor("releaseSnapshot")![0].Should().Be("capture-1");
        _runtime.Export.Invoked("exportSnapshot").Should().Be(0);
    }

    [Test]
    public void CaptureFailure_IsReportedWithoutOpeningTheDialog()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = null;
        var cut = RenderCard(Chart(series));
        WaitForChart(cut);

        ClickExport(cut);

        cut.WaitForAssertion(
            () => _runtime.Export.Invoked("captureSnapshot").Should().Be(1),
            TimeSpan.FromSeconds(5));
        _snackbar.Received(1).Add(
            Arg.Is<string>(message => message.Contains("could not be prepared for export")),
            Severity.Error,
            Arg.Any<Action<SnackbarOptions>?>(),
            Arg.Any<string?>());
        _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty();
        _runtime.Export.Invoked("releaseSnapshot").Should().Be(0,
            "a failed capture has nothing to release");
    }

    [Test]
    public void CaptureError_IsReportedAndTheControlBecomesUsableAgain()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Export.Handler = (identifier, _) => identifier == "captureSnapshot"
            ? Task.FromException<object?>(new JSException("capture failed"))
            : Task.FromResult<object?>(null);
        var cut = RenderCard(Chart(series));
        WaitForChart(cut);

        ClickExport(cut);

        cut.WaitForAssertion(
            () => _snackbar.ReceivedCalls().Should().ContainSingle(),
            TimeSpan.FromSeconds(5));
        _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty();
        cut.Find("button[title='Export chart data or image']").HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public void Dialog_ShowsCapturedNamesWithTheConfiguredSourceHint()
    {
        var first = Series("Temp", "#F97316", "factory/line-a", "temperature");
        var second = Series("Temp", "#A78BFA", "factory/line-b", "temperature");
        Record(first, 2, 21.5);
        Record(second, 1, 22.5);
        _runtime.Snapshot = new ChartExportSnapshot(
            "capture-1", MessageTimePreferences.ZoneUtc, MessageTimePreferences.ClockFormat24Hour,
            [
                new ChartExportSeriesSummary(first.Id.ToString(), "Applied temp", first.Color, 2),
                new ChartExportSeriesSummary(second.Id.ToString(), "Temp", second.Color, 0),
            ]);
        var cut = RenderCard(Chart(first, second));
        WaitForChart(cut);

        ClickExport(cut);
        WaitForDialog();

        _dialogProvider.FindAll(".chart-export__name").Select(span => span.TextContent.Trim())
            .Should().Equal(["Applied temp", "Temp"],
                "the dialog describes the captured snapshot, not the current configuration");
        _dialogProvider.FindAll(".chart-export__hint").Select(span => span.TextContent.Trim())
            .Should().Equal("factory/line-a › temperature", "factory/line-b › temperature");
        _dialogProvider.Find(".chart-export__summary").TextContent
            .Should().Contain("2 of 2 series selected");
    }

    [Test]
    public void Export_SendsTheSelectionFormatAndFileName_WithoutTouchingTheLiveChart()
    {
        var first = Series("Temp", "#F97316", "factory/line-a", "temperature");
        var second = Series("Flow", "#A78BFA", "factory/line-b", "flow");
        Record(first, 2, 21.5);
        Record(second, 1, 3);
        _runtime.Snapshot = Snapshot("capture-1", first, second);
        var chart = Chart(first, second);
        var cut = RenderCard(chart);
        WaitForChart(cut);
        var updatesBefore = _runtime.Renderer.Invoked("updateChart");

        ClickExport(cut);
        WaitForDialog();
        _dialogProvider.FindAll(".mud-dialog input[type='checkbox']")[1].Change(false);
        _dialogProvider.FindAll(".mud-dialog .mud-toggle-item")
            .Single(item => item.TextContent.Trim() == "PNG").Click();
        ClickDialogAction("Export");

        cut.WaitForAssertion(
            () => _runtime.Export.Invoked("exportSnapshot").Should().Be(1),
            TimeSpan.FromSeconds(5));

        var call = _runtime.Export.ArgsFor("exportSnapshot")!;
        call[0].Should().Be("capture-1");
        call[1].Should().BeEquivalentTo(new[] { first.Id.ToString() });
        call[2].Should().Be("png");
        call[3].Should().Be("Chart-20260101-120000Z.png");
        chart.Series.Select(series => series.Id).Should().Equal([first.Id, second.Id],
            "export selection must not change the live chart");
        _runtime.Renderer.Invoked("updateChart").Should().Be(updatesBefore);
        cut.WaitForAssertion(
            () => _runtime.Export.Invoked("releaseSnapshot").Should().Be(1),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void PausedChart_ExportsTheAppliedSnapshot()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        var cut = RenderCard(Chart(series));
        WaitForChart(cut);

        cut.Find("button[title='Pause live updates (keeps the view still)']").Click();
        cut.WaitForAssertion(
            () => cut.Find(".chart-card-status .mud-chip").TextContent.Trim().Should().Be("Paused"),
            TimeSpan.FromSeconds(5));

        ClickExport(cut);
        WaitForDialog();

        _runtime.Export.Invoked("captureSnapshot").Should().Be(1);
        cut.Find("button[title='Resume live updates']").Should().NotBeNull(
            "opening the export dialog must not resume the paused chart");
        _runtime.Renderer.Invoked("updateChart").Should().Be(0);
    }

    [Test]
    public async Task DisposingTheCard_ReleasesAnOpenCaptureAndTheExportInterop()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        var cut = RenderCard(Chart(series));
        WaitForChart(cut);
        ClickExport(cut);
        WaitForDialog();

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());

        _dialogProvider.WaitForAssertion(
            () => _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(),
            TimeSpan.FromSeconds(5));
        _runtime.Export.Invoked("releaseSnapshot").Should().Be(1);
        _runtime.Export.ArgsFor("releaseSnapshot")![0].Should().Be("capture-1");
        _runtime.Export.Disposed.Should().Be(1);
        _runtime.Renderer.Invoked("disposeChart").Should().Be(1);
    }

    [Test]
    public async Task DisposingDuringCapture_ReleasesTheSnapshotAndOpensNoDialog()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtime.Export.Handler = async (identifier, _) =>
        {
            if (identifier != "captureSnapshot")
                return null;

            started.TrySetResult();
            await resume.Task;
            return _runtime.Snapshot;
        };
        var cut = RenderCard(Chart(series));
        WaitForChart(cut);
        ClickExport(cut);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        dispose.IsCompleted.Should().BeFalse(
            "export disposal waits on the interop gate held by the in-flight capture");

        resume.SetResult();
        cut.WaitForAssertion(() => dispose.IsCompleted.Should().BeTrue(), TimeSpan.FromSeconds(5));
        await dispose;

        _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty();
        _runtime.Export.Invoked("releaseSnapshot").Should().Be(1);
        _runtime.Export.ArgsFor("releaseSnapshot")![0].Should().Be("capture-1");
        _runtime.Export.Invoked("exportSnapshot").Should().Be(0);
        _runtime.Export.Disposed.Should().Be(1);
        _snackbar.ReceivedCalls().Should().BeEmpty(
            "a capture superseded by disposal is not an error");
    }

    [Test]
    public async Task RemovingThePlotDuringCapture_ReleasesTheSnapshotAndOpensNoDialog()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtime.Export.Handler = async (identifier, _) =>
        {
            if (identifier != "captureSnapshot")
                return null;

            started.TrySetResult();
            await resume.Task;
            return _runtime.Snapshot;
        };
        var chart = Chart(series);
        var cut = RenderCard(chart);
        WaitForChart(cut);
        ClickExport(cut);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cut.Render(parameters => parameters
            .Add(card => card.Configuration, Chart())
            .Add(card => card.Clock, _clock));

        resume.SetResult();

        cut.WaitForAssertion(
            () => _runtime.Export.Invoked("releaseSnapshot").Should().Be(1),
            TimeSpan.FromSeconds(5));
        _runtime.Export.ArgsFor("releaseSnapshot")![0].Should().Be("capture-1");
        _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty();
        _runtime.Export.Invoked("exportSnapshot").Should().Be(0);
        _snackbar.ReceivedCalls().Should().BeEmpty(
            "a capture superseded by a removed plot is not an error");
    }

    [Test]
    public void RemovingThePlotWithTheDialogOpen_ClosesItAndReleasesTheCapture()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        var chart = Chart(series);
        var cut = RenderCard(chart);
        WaitForChart(cut);
        ClickExport(cut);
        WaitForDialog();

        cut.Render(parameters => parameters
            .Add(card => card.Configuration, Chart())
            .Add(card => card.Clock, _clock));

        cut.WaitForAssertion(
            () => _dialogProvider.FindAll(".mud-dialog").Should().BeEmpty(),
            TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(
            () => _runtime.Export.Invoked("releaseSnapshot").Should().Be(1),
            TimeSpan.FromSeconds(5));
        _runtime.Export.ArgsFor("releaseSnapshot")![0].Should().Be("capture-1");
        _runtime.Export.Invoked("exportSnapshot").Should().Be(0);
    }

    [Test]
    public async Task ExportCallbackAfterDisposal_DoesNotRecreateTheInterop()
    {
        var series = Series("Temp");
        Record(series, 1, 21.5);
        _runtime.Snapshot = Snapshot("capture-1", series);
        var cut = RenderCard(Chart(series));
        WaitForChart(cut);
        ClickExport(cut);
        WaitForDialog();

        var export = _dialogProvider.FindComponent<ChartExportDialogComponent>().Instance.Export;
        export.Should().NotBeNull();
        var importsBefore = _runtime.Imports;

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());

        var request = new ChartExportDialogComponent.ExportRequest(["series-1"], "csv", "chart.csv");
        var invoke = () => export!(request);

        await invoke.Should().ThrowAsync<InvalidOperationException>();
        _runtime.Export.Invoked("exportSnapshot").Should().Be(0);
        _runtime.Imports.Should().Be(importsBefore,
            "a disposed card must not recreate the export interop");
    }

    private sealed class ChartRuntime(IJSRuntime inner) : IJSRuntime
    {
        internal ChartModule Renderer { get; } = new();
        internal ChartModule Export { get; } = new();
        internal ChartExportSnapshot? Snapshot { get; set; }
        internal int Imports { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ResolveModule(identifier, args) is { } module
                ? new ValueTask<TValue>((TValue)(object)module)
                : inner.InvokeAsync<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            ResolveModule(identifier, args) is { } module
                ? new ValueTask<TValue>((TValue)(object)module)
                : inner.InvokeAsync<TValue>(identifier, cancellationToken, args);

        private ChartModule? ResolveModule(string identifier, object?[]? args)
        {
            if (identifier != "import" || args is not [string path])
                return null;

            Imports++;
            return path switch
            {
                MqttProbe.UI.Components.Charts.ChartRendererInterop.ModulePath => Renderer,
                MqttProbe.UI.Components.Charts.ChartExportInterop.ModulePath => Export,
                _ => null,
            };
        }
    }

    private sealed class ChartModule : IJSObjectReference
    {
        private readonly List<(string Identifier, object?[]? Args)> _invocations = [];

        internal Func<string, object?[]?, Task<object?>> Handler { get; set; } =
            (_, _) => Task.FromResult<object?>(null);

        internal int Disposed { get; private set; }

        internal int Invoked(string identifier) =>
            _invocations.Count(entry => entry.Identifier == identifier);

        internal object?[]? ArgsFor(string identifier) =>
            _invocations.LastOrDefault(entry => entry.Identifier == identifier).Args;

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            _invocations.Add((identifier, args));
            var result = await Handler(identifier, args);
            return result is null ? default! : (TValue)result;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }
}
