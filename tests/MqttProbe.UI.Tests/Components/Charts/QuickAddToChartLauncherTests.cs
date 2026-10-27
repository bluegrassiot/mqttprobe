using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class QuickAddToChartLauncherTests : BunitTestContext
{
    [Test]
    public async Task ShowAsync_OpensQuickAddDialogWithSharedDefaults()
    {
        var dialogService = Substitute.For<IDialogService>();
        var snackbar = Substitute.For<ISnackbar>();
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));

        string? title = null;
        DialogParameters? parameters = null;
        DialogOptions? options = null;
        dialogService.ShowAsync<QuickAddToChartDialog>(
                Arg.Do<string?>(value => title = value),
                Arg.Do<DialogParameters>(value => parameters = value),
                Arg.Do<DialogOptions>(value => options = value))
            .Returns(dialogRef);

        await QuickAddToChartLauncher.ShowAsync(
            dialogService, snackbar,
            "spBv1.0/factory/NDATA/edge-01",
            "metrics.Flow Rate",
            "Flow Rate");

        title.Should().Be("Chart Field");
        parameters.Should().NotBeNull();
        parameters!.Get<string>("Topic").Should().Be("spBv1.0/factory/NDATA/edge-01");
        parameters.Get<string>("JsonPath").Should().Be("metrics.Flow Rate");
        parameters.Get<string>("SeriesName").Should().Be("Flow Rate");
        options.Should().NotBeNull();
        options!.MaxWidth.Should().Be(MaxWidth.Small);
        options.FullWidth.Should().BeTrue();
    }
}
