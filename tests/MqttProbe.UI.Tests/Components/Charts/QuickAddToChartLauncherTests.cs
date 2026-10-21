using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class QuickAddToChartLauncherTests : BunitTestContext
{
    private const string Topic = "spBv1.0/factory/NDATA/edge-01";
    private const string JsonPath = "metrics.CPU Usage (%)";
    private const string SeriesName = "CPU Usage (%)";

    private IChartSettings _chartStore = null!;
    private ISnackbar _snackbar = null!;
    private static readonly Guid _connectionId = Guid.NewGuid();

    [SetUp]
    public void SetupMocks()
    {
        var sessionState = Substitute.For<ISessionState>();
        sessionState.SelectedConnection.Returns(new Connection { Id = _connectionId });

        _chartStore = Substitute.For<IChartSettings>();
        _chartStore.GetCharts(_connectionId).Returns([]);
        _chartStore.AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        _chartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        Services.AddChartSettings(_chartStore);
        Services.AddSingleton(sessionState);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());

        _snackbar = Substitute.For<ISnackbar>();
        Services.AddSingleton(_snackbar);

        EnsureMudProviders();
    }

    private static IDialogService SubstitutedDialogService(DialogResult result, Action<DialogParameters>? capture = null)
    {
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(result));

        var dialogService = Substitute.For<IDialogService>();
        dialogService
            .ShowAsync<QuickAddToChartDialog>(
                Arg.Any<string>(),
                Arg.Do<DialogParameters>(p => capture?.Invoke(p)),
                Arg.Any<DialogOptions>())
            .Returns(Task.FromResult(dialogReference));
        return dialogService;
    }

    [Test]
    public async Task OpenAsync_PassesTopicPathAndSeriesNameToDialog()
    {
        DialogParameters? captured = null;
        var dialogService = SubstitutedDialogService(DialogResult.Cancel(), p => captured = p);

        await QuickAddToChartLauncher.OpenAsync(
            dialogService, _snackbar, Topic, new ChartFieldSelection(JsonPath, SeriesName));

        captured.Should().NotBeNull();
        var typed = (DialogParameters<QuickAddToChartDialog>)captured!;
        typed.Get(x => x.Topic).Should().Be(Topic);
        typed.Get(x => x.JsonPath).Should().Be(JsonPath);
        typed.Get(x => x.SeriesName).Should().Be(SeriesName);
    }

    [Test]
    public async Task OpenAsync_WhenDialogConfirms_ShowsSuccessSnackbar()
    {
        var dialogService = SubstitutedDialogService(DialogResult.Ok("Chart created: factory edge-01"));

        await QuickAddToChartLauncher.OpenAsync(
            dialogService, _snackbar, Topic, new ChartFieldSelection(JsonPath, SeriesName));

        _snackbar.Received(1).Add("Chart created: factory edge-01", Severity.Success,
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task OpenAsync_WhenDialogCancels_ShowsNoSnackbar()
    {
        var dialogService = SubstitutedDialogService(DialogResult.Cancel());

        await QuickAddToChartLauncher.OpenAsync(
            dialogService, _snackbar, Topic, new ChartFieldSelection(JsonPath, SeriesName));

        _snackbar.DidNotReceive().Add(Arg.Any<string>(), Arg.Any<Severity>(),
            Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task OpenAsync_ShowsTopicPathAndSeriesNameInDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        await provider.InvokeAsync(async () => await dialogService.ShowAsync<QuickAddToChartDialog>(
            "Chart Field",
            QuickAddToChartLauncher.BuildParameters(Topic, new ChartFieldSelection(JsonPath, SeriesName)),
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true }));

        provider.Markup.Should().Contain($"{Topic} › {JsonPath}");
        provider.FindAll("input").Should().Contain(i => i.GetAttribute("value") == SeriesName);
    }

    [Test]
    public async Task OpenAsync_ReusesDialogDefaultChartName_ForSparkplugTopic()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        await provider.InvokeAsync(async () => await dialogService.ShowAsync<QuickAddToChartDialog>(
            "Chart Field",
            QuickAddToChartLauncher.BuildParameters(Topic, new ChartFieldSelection(JsonPath, SeriesName)),
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true }));

        provider.FindAll("input").Should().Contain(i => i.GetAttribute("value") == "factory edge-01");
    }

    [Test]
    public void SeriesLabel_WithoutSeriesName_FallsBackToLastPathSegment()
    {
        QuickAddToChartLauncher.SeriesLabel(new ChartFieldSelection("metrics.CPU Usage (%)"))
            .Should().Be("CPU Usage (%)");
    }

    [Test]
    public void SeriesLabel_WithSeriesName_UsesSeriesName()
    {
        QuickAddToChartLauncher.SeriesLabel(new ChartFieldSelection("metrics.value", "Motor Speed"))
            .Should().Be("Motor Speed");
    }
}
