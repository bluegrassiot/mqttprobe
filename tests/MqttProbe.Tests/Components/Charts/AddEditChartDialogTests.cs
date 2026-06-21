using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Chart;
using MqttProbe.Services.Chart;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Charts;

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
        var config = new ChartConfiguration { Name = "Temps", Type = MqttProbe.Models.Chart.ChartType.Area, MaxPoints = 200 };

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
        config.Type.Should().Be(MqttProbe.Models.Chart.ChartType.Line);
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
}
