using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Chart;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Extensions;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class AddEditChartDialogTests : BunitTestContext
{
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;

    [SetUp]
    public void SetupMocks()
    {
        var mockFieldRegistry = Substitute.For<IChartFieldRegistry>();
        mockFieldRegistry.GetTopics().Returns([]);
        Services.AddSingleton(mockFieldRegistry);

        EnsureMudProviders();
        _dialogProvider = Render<MudDialogProvider>();
    }

    private async Task<IDialogReference> OpenDialog(ChartConfiguration? config = null)
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        IDialogReference? dialogRef = null;

        var parameters = new DialogParameters<AddEditChartDialog>();
        if (config != null)
            parameters.Add(d => d.Configuration, config);

        await _dialogProvider.InvokeAsync(async () =>
        {
            dialogRef = await dialogService.ShowAsync<AddEditChartDialog>("Chart", parameters,
                new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        });

        return dialogRef!;
    }

    [Test]
    public async Task Renders_WithNewChartDefaults_WhenConfigurationIsNull()
    {
        await OpenDialog();

        _dialogProvider.Markup.Should().Contain("New Chart");
        _dialogProvider.Markup.Should().Contain("Create");
    }

    [Test]
    public async Task Renders_WithExistingValues_WhenConfigurationIsProvided()
    {
        var config = new ChartConfiguration { Name = "Temps", Type = MqttProbe.Core.Models.Chart.ChartType.Area, MaxPoints = 200 };

        await OpenDialog(config);

        _dialogProvider.Markup.Should().Contain("Edit Chart");
        _dialogProvider.Markup.Should().Contain("Temps");
        _dialogProvider.Markup.Should().Contain("Save");
    }

    [Test]
    public async Task Cancel_ClosesDialog_WithCancelledResult()
    {
        var dialogRef = await OpenDialog();

        _dialogProvider.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        var result = await dialogRef.Result;
        result.Should().NotBeNull();
        result.Canceled.Should().BeTrue();
    }

    [Test]
    public async Task Submit_WithEmptyName_DisablesButton()
    {
        await OpenDialog();

        // The name field is empty by default — the submit button should be disabled
        var submitBtn = _dialogProvider.FindAll("button")
            .First(b => b.TextContent.Contains("Create"));
        submitBtn.GetAttribute("disabled").Should().NotBeNull();
    }

    [Test]
    public async Task Submit_CreatesNewChartConfiguration_WithNewIdAndExpectedFields()
    {
        var dialogRef = await OpenDialog();

        _dialogProvider.FindAll("input")[0].Input("My New Chart");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create")).Click();

        var result = await dialogRef.Result;
        result.Should().NotBeNull();
        result.Canceled.Should().BeFalse();
        var config = result.Data.Should().BeAssignableTo<ChartConfiguration>().Subject;
        config.Id.Should().NotBe(Guid.Empty);
        config.Name.Should().Be("My New Chart");
        config.Type.Should().Be(MqttProbe.Core.Models.Chart.ChartType.Line);
    }

    [Test]
    public async Task ActionButtons_CarryOptInWidthClass()
    {
        await OpenDialog();

        _dialogProvider.Markup.Should().Contain("app-btn-min");
    }

    [Test]
    public async Task Submit_EditsExistingChartConfiguration_PreservesIdAndUpdatesFields()
    {
        var original = new ChartConfiguration { Name = "Old Name" };
        var dialogRef = await OpenDialog(original);

        _dialogProvider.FindAll("input")[0].Input("New Name");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        var result = await dialogRef.Result;
        result.Should().NotBeNull();
        result.Canceled.Should().BeFalse();
        var config = result.Data.Should().BeAssignableTo<ChartConfiguration>().Subject;
        config.Id.Should().Be(original.Id);
        config.Name.Should().Be("New Name");
    }

    [Test]
    public async Task StaleTimeout_NewChart_DefaultsTo10SecondsAndDetectionOn()
    {
        await OpenDialog();

        _dialogProvider.Markup.Should().Contain("Stale timeout (seconds)");
        _dialogProvider.Markup.Should().Contain("Off");
        _dialogProvider.FindComponent<MudNumericField<double?>>().Instance.GetState(x => x.Value).Should().Be(10);
        TimeoutInput().HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task StaleTimeout_ExistingChart_RestoresConfiguredSeconds()
    {
        await OpenDialog(new ChartConfiguration { Name = "Flow", StaleTimeoutSeconds = 25 });

        _dialogProvider.FindComponent<MudNumericField<double?>>().Instance.GetState(x => x.Value).Should().Be(25);
    }

    [Test]
    public async Task StaleTimeout_ExistingChartWithoutSetting_RendersTenSeconds()
    {
        await OpenDialog(new ChartConfiguration { Name = "Legacy" });

        _dialogProvider.FindComponent<MudNumericField<double?>>().Instance.GetState(x => x.Value).Should().Be(10);
    }

    [Test]
    public async Task Submit_SavesConfiguredStaleTimeout()
    {
        var dialogRef = await OpenDialog();

        _dialogProvider.FindAll("input")[0].Input("Flow");
        TimeoutInput().Change("30");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create")).Click();

        var result = await dialogRef.Result;
        var config = result!.Data.Should().BeAssignableTo<ChartConfiguration>().Subject;
        config.StaleTimeoutSeconds.Should().Be(30);
    }

    [Test]
    public async Task Submit_WithDetectionOff_SavesNoTimeout()
    {
        var dialogRef = await OpenDialog(new ChartConfiguration { Name = "On change only", StaleTimeoutSeconds = 30 });

        OffSwitch().Change(true);
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        var result = await dialogRef.Result;
        var config = result!.Data.Should().BeAssignableTo<ChartConfiguration>().Subject;
        config.StaleTimeoutSeconds.Should().BeNull();
    }

    [Test]
    public async Task EditingChartWithDetectionOff_ShowsOffAndDisabledTimeout()
    {
        await OpenDialog(new ChartConfiguration { Name = "On change only", StaleTimeoutSeconds = null });

        TimeoutInput().HasAttribute("disabled").Should().BeTrue("an Off chart has no timeout to edit");
        OffSwitch().HasAttribute("checked").Should().BeTrue();
    }

    [Test]
    public async Task Submit_WithEmptyTimeout_IsDisabled()
    {
        await OpenDialog();

        _dialogProvider.FindAll("input")[0].Input("Flow");
        TimeoutInput().Change("");

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create"))
            .GetAttribute("disabled").Should().NotBeNull("a timeout is required unless detection is Off");
    }

    [Test]
    public async Task Submit_WithZeroTimeout_IsDisabled()
    {
        await OpenDialog();

        _dialogProvider.FindAll("input")[0].Input("Flow");
        TimeoutInput().Change("0");

        _dialogProvider.Markup.Should().Contain("Enter a timeout greater than zero seconds.");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create"))
            .GetAttribute("disabled").Should().NotBeNull();
    }

    private AngleSharp.Dom.IElement TimeoutInput() =>
        _dialogProvider.Find(".chart-dialog-timeout input");

    private AngleSharp.Dom.IElement OffSwitch() =>
        _dialogProvider.Find(".chart-dialog-timeout-row input[type='checkbox']");
}
