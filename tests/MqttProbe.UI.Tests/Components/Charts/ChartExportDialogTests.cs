using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using ChartExportDialogComponent = MqttProbe.UI.Components.Charts.ChartExportDialog;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartExportDialogTests : BunitTestContext
{
    private static readonly DateTimeOffset _start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private FakeTimeProvider _clock = null!;
    private Func<ChartExportDialogComponent.ExportRequest, Task> _onExport = _ => Task.CompletedTask;
    private ChartExportDialogComponent.ExportRequest? _request;

    [SetUp]
    public void SetupMocks()
    {
        _clock = new FakeTimeProvider(_start);
        EnsureMudProviders();
        _dialogProvider = Render<MudDialogProvider>();
    }

    private static IReadOnlyList<ChartExportDialogComponent.SeriesOption> DuplicateSeries() =>
    [
        new()
        {
            Id = "series-a",
            Name = "Temp",
            Color = "#F97316",
            PointCount = 2,
            Hint = "factory/line-a › temperature",
        },
        new()
        {
            Id = "series-b",
            Name = "Temp",
            Color = "#A78BFA",
            PointCount = 0,
            Hint = "factory/line-b › temperature",
        },
    ];

    private async Task<IDialogReference> OpenDialog(
        IReadOnlyList<ChartExportDialogComponent.SeriesOption>? series = null,
        string chartName = "Chart")
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        var parameters = new DialogParameters<ChartExportDialogComponent>
        {
            { dialog => dialog.ChartName, chartName },
            { dialog => dialog.Clock, _clock },
            { dialog => dialog.Series, series ?? DuplicateSeries() },
            {
                dialog => dialog.Export,
                request =>
                {
                    _request = request;
                    return _onExport(request);
                }
            },
        };

        await _dialogProvider.InvokeAsync(async () =>
        {
            reference = await dialogService.ShowAsync<ChartExportDialogComponent>(
                "Export chart",
                parameters,
                new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        });

        return reference!;
    }

    private IReadOnlyList<AngleSharp.Dom.IElement> Checkboxes() =>
        _dialogProvider.FindAll(".mud-dialog input[type='checkbox']");

    private AngleSharp.Dom.IElement DialogAction(string label) =>
        _dialogProvider.FindAll(".mud-dialog-actions button")
            .Single(button => button.TextContent.Trim() == label);

    private AngleSharp.Dom.IElement SelectionAction(string label) =>
        _dialogProvider.FindAll(".chart-export__selection-actions button")
            .Single(button => button.TextContent.Trim() == label);

    private AngleSharp.Dom.IElement Toggle(string label) =>
        _dialogProvider.FindAll(".chart-export__formats .mud-toggle-item")
            .Single(item => item.TextContent.Trim() == label);

    private string Summary() => _dialogProvider.Find(".chart-export__summary").TextContent.Trim();

    private string FileName() =>
        _dialogProvider.Find(".chart-export__filename .app-mono").TextContent.Trim();

    private bool ExportDisabled() => DialogAction("Export").HasAttribute("disabled");

    [Test]
    public async Task EverySeriesStartsSelected_AndRepeatedNamesCarryTheirSource()
    {
        await OpenDialog();

        _dialogProvider.FindAll(".chart-export__name").Select(span => span.TextContent.Trim())
            .Should().Equal("Temp", "Temp");
        _dialogProvider.FindAll(".chart-export__hint").Select(span => span.TextContent.Trim())
            .Should().Equal("factory/line-a › temperature", "factory/line-b › temperature");
        _dialogProvider.FindAll(".chart-export__count").Select(span => span.TextContent.Trim())
            .Should().Equal("2 points", "No points");
        Checkboxes().Should().HaveCount(2);
        Checkboxes().Should().OnlyContain(input => input.HasAttribute("checked"));
        Summary().Should().Contain("2 of 2 series selected");
        ExportDisabled().Should().BeFalse();
    }

    [Test]
    public async Task ClearDisablesExport_AndSelectAllRestoresIt()
    {
        await OpenDialog();

        SelectionAction("Clear").Click();

        Summary().Should().Contain("0 of 2 series selected");
        ExportDisabled().Should().BeTrue("there is nothing to export");
        Checkboxes().Should().NotContain(input => input.HasAttribute("checked"));

        SelectionAction("Select all").Click();

        Summary().Should().Contain("2 of 2 series selected");
        ExportDisabled().Should().BeFalse();
    }

    [Test]
    public async Task UncheckingEverySeries_DisablesExport()
    {
        await OpenDialog();

        foreach (var checkbox in Checkboxes())
            checkbox.Change(false);

        Summary().Should().Contain("0 of 2 series selected");
        ExportDisabled().Should().BeTrue();
    }

    [Test]
    public async Task ExportSendsTheSelectedSeriesAndTheCsvFile()
    {
        var dialog = await OpenDialog();

        Checkboxes()[1].Change(false);
        DialogAction("Export").Click();

        var result = await dialog.Result;
        result!.Canceled.Should().BeFalse();
        _request.Should().NotBeNull();
        _request!.SeriesIds.Should().Equal("series-a");
        _request.Format.Should().Be("csv");
        _request.FileName.Should().Be("Chart-20260101-120000Z.csv");
    }

    [TestCase("PNG", "png")]
    [TestCase("SVG", "svg")]
    public async Task ImageFormats_SendTheirExtensionAndDescribeTheCapturedZoom(
        string label,
        string extension)
    {
        var dialog = await OpenDialog();

        Toggle(label).Click();

        _dialogProvider.Markup.Should().Contain("captured chart view and zoom");
        FileName().Should().Be($"Chart-20260101-120000Z.{extension}");

        DialogAction("Export").Click();

        (await dialog.Result)!.Canceled.Should().BeFalse();
        _request!.Format.Should().Be(extension);
        _request.FileName.Should().EndWith($".{extension}");
    }

    [Test]
    public async Task CsvFormat_ExplainsTheColumnsAndTheImportStep()
    {
        await OpenDialog();

        _dialogProvider.Markup.Should().Contain("timestamp, UTC offset");
        _dialogProvider.Markup.Should().Contain("date import step");
        FileName().Should().Be("Chart-20260101-120000Z.csv");
    }

    [Test]
    public async Task UnsafeChartNameCharacters_AreReplacedInTheFileName()
    {
        await OpenDialog(chartName: "Boiler/room: 1");

        FileName().Should().Be("Boiler-room-1-20260101-120000Z.csv");
    }

    [Test]
    public async Task ExportFailure_KeepsTheDialogOpenWithTheProblem()
    {
        _onExport = _ => Task.FromException(new InvalidOperationException("image rendering failed"));
        var dialog = await OpenDialog();

        DialogAction("Export").Click();

        _dialogProvider.WaitForAssertion(
            () => _dialogProvider.Markup.Should().Contain("The export could not finish"),
            TimeSpan.FromSeconds(5));
        _dialogProvider.Markup.Should().Contain("image rendering failed");
        ExportDisabled().Should().BeFalse("the user can retry");
        dialog.Result.IsCompleted.Should().BeFalse("a failed export must not close as a success");
    }

    [Test]
    public async Task BusyExport_ShowsProgressAndBlocksCancel()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _onExport = async _ => await gate.Task;
        var dialog = await OpenDialog();

        DialogAction("Export").Click();

        _dialogProvider.WaitForAssertion(
            () => DialogAction("Exporting…").HasAttribute("disabled").Should().BeTrue(),
            TimeSpan.FromSeconds(5));
        DialogAction("Cancel").HasAttribute("disabled").Should().BeTrue();

        await _dialogProvider.InvokeAsync(gate.SetResult);

        (await dialog.Result)!.Canceled.Should().BeFalse();
    }

    [Test]
    public async Task Cancel_ClosesWithoutExporting()
    {
        var dialog = await OpenDialog();

        DialogAction("Cancel").Click();

        (await dialog.Result)!.Canceled.Should().BeTrue();
        _request.Should().BeNull();
    }

    [Test]
    public async Task ChartWithoutSeries_CannotBeExported()
    {
        await OpenDialog(series: []);

        _dialogProvider.Markup.Should().Contain("no series to export");
        Summary().Should().Contain("Nothing to select");
        ExportDisabled().Should().BeTrue();
        SelectionAction("Select all").HasAttribute("disabled").Should().BeTrue();
    }
}
