using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartQuickAddTests
{
    private static (IDialogService DialogService, ISnackbar Snackbar, Func<DialogParameters?> Captured)
        CreateServices(DialogResult? result)
    {
        var dialogService = Substitute.For<IDialogService>();
        var snackbar = Substitute.For<ISnackbar>();
        DialogParameters? captured = null;

        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(Task.FromResult(result));
        dialogService
            .ShowAsync<QuickAddToChartDialog>(
                Arg.Any<string>(),
                Arg.Do<DialogParameters>(p => captured = p),
                Arg.Any<DialogOptions>())
            .Returns(Task.FromResult(reference));

        return (dialogService, snackbar, () => captured);
    }

    [Test]
    public async Task AddToChartAsync_PassesTopicPathAndSeriesNameToDialog()
    {
        var (dialogService, snackbar, captured) = CreateServices(DialogResult.Cancel());

        await ChartQuickAdd.AddToChartAsync(
            dialogService, snackbar, "spBv1.0/Plant1/NDATA/Node-2", "metrics.Motor Speed", "Motor Speed");

        captured().Should().NotBeNull();
        var typed = (DialogParameters<QuickAddToChartDialog>)captured()!;
        typed.Get(x => x.Topic).Should().Be("spBv1.0/Plant1/NDATA/Node-2");
        typed.Get(x => x.JsonPath).Should().Be("metrics.Motor Speed");
        typed.Get(x => x.SeriesName).Should().Be("Motor Speed");
    }

    [Test]
    public async Task AddToChartAsync_WhenDialogSucceeds_ShowsSuccessSnackbar()
    {
        var (dialogService, snackbar, _) = CreateServices(DialogResult.Ok("Series added to chart: Plant1 Node-2"));

        await ChartQuickAdd.AddToChartAsync(
            dialogService, snackbar, "spBv1.0/Plant1/NDATA/Node-2", "metrics.Temp", "Temp");

        snackbar.Received(1).Add(
            "Series added to chart: Plant1 Node-2", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task AddToChartAsync_WhenDialogIsCancelled_ShowsNoSnackbar()
    {
        var (dialogService, snackbar, _) = CreateServices(DialogResult.Cancel());

        await ChartQuickAdd.AddToChartAsync(
            dialogService, snackbar, "spBv1.0/Plant1/NDATA/Node-2", "metrics.Temp", "Temp");

        snackbar.DidNotReceive().Add(
            Arg.Any<string>(), Arg.Any<Severity>(), Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }
}
