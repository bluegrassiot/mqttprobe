using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;
using MudBlazor.Extensions;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class QuickAddToChartDialogTests : BunitTestContext
{
    private IChartSettings _mockChartStore = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private static readonly Guid _testConnectionId = Guid.NewGuid();

    [SetUp]
    public void SetupMocks()
    {
        var mockSessionState = Substitute.For<ISessionState>();
        mockSessionState.SelectedConnection.Returns(new Connection { Id = _testConnectionId });

        _mockChartStore = Substitute.For<IChartSettings>();
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        _mockChartStore.AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        Services.AddChartSettings(_mockChartStore);
        Services.AddSingleton(mockSessionState);
        Services.AddSingleton(Substitute.For<IUxMetricsService>());

        EnsureMudProviders();
        _dialogProvider = Render<MudDialogProvider>();
    }

    private async Task<IDialogReference> OpenDialog(string topic = "sensors/temp", string jsonPath = "$.temp", string seriesName = "temp")
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        IDialogReference? dialogRef = null;

        var parameters = new DialogParameters<QuickAddToChartDialog>
        {
            { d => d.Topic, topic },
            { d => d.JsonPath, jsonPath },
            { d => d.SeriesName, seriesName }
        };

        await _dialogProvider.InvokeAsync(async () =>
        {
            dialogRef = await dialogService.ShowAsync<QuickAddToChartDialog>("Chart Field", parameters,
                new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        });

        return dialogRef!;
    }

    [Test]
    public async Task OnInitialized_WithNoExistingCharts_DefaultsToCreateNewMode()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);

        await OpenDialog();

        // CreateNew mode: "Chart Name" field is visible
        _dialogProvider.Markup.Should().Contain("Chart Name");
    }

    [Test]
    public async Task OnInitialized_WithOneExistingChart_DefaultsToAddToExistingMode_AndSelectsIt()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart" };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);

        await OpenDialog();

        // AddToExisting mode: the chart name appears in the selection list
        _dialogProvider.Markup.Should().Contain("Existing Chart");
        // Submit button should be enabled (chart is auto-selected)
        var submitBtn = _dialogProvider.FindAll("button")
            .First(b => b.TextContent.Contains("Add Series"));
        submitBtn.GetAttribute("disabled").Should().BeNull("the only chart should be auto-selected");
    }

    [Test]
    public async Task CanSubmit_CreateNew_FalseWhenNameIsEmpty()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);

        // Pass empty seriesName so _newChartName defaults to "" → CanSubmit = false
        await OpenDialog(seriesName: "");

        // CreateNew mode with empty name: submit is disabled
        var submitBtn = _dialogProvider.FindAll("button")
            .First(b => b.TextContent.Contains("Create Chart"));
        submitBtn.GetAttribute("disabled").Should().NotBeNull();
    }

    [Test]
    public async Task CanSubmit_AddToExisting_FalseWhenNoChartSelected()
    {
        // Two charts so none is auto-selected
        var chart1 = new ChartConfiguration { Name = "Chart A" };
        var chart2 = new ChartConfiguration { Name = "Chart B" };
        _mockChartStore.GetCharts(_testConnectionId).Returns([chart1, chart2]);

        await OpenDialog();

        var submitBtn = _dialogProvider.FindAll("button")
            .First(b => b.TextContent.Contains("Add Series"));
        submitBtn.GetAttribute("disabled").Should().NotBeNull("no chart is selected so CanSubmit is false");
    }

    [TestCase("spBv1.0/Plant1/DDATA/Node-2/Device-1", "Metric-1", ExpectedResult = "Plant1 Node-2 Device-1")]
    [TestCase("spBv1.0/Plant1/NDATA/Node-2", "Metric-1", ExpectedResult = "Plant1 Node-2")]
    [TestCase("spBv1.0/Plant1/NBIRTH/Node-2", "Metric-1", ExpectedResult = "Plant1 Node-2")]
    [TestCase("sensors/temp", "temp", ExpectedResult = "temp")]
    public string DefaultChartName_ReturnsExpectedTopicTitle(string topic, string seriesName) =>
        QuickAddToChartDialog.DefaultChartName(topic, seriesName);

    [Test]
    public async Task OnInitialized_CreateNew_WithSparkplugDeviceTopic_DefaultsChartNameToGroupNodeDevice()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);

        await OpenDialog(
            topic: "spBv1.0/Plant1/DDATA/Node-2/Device-1",
            jsonPath: "metrics.Metric-1",
            seriesName: "Metric-1");

        ChartNameInput().GetAttribute("value").Should().Be("Plant1 Node-2 Device-1");
    }

    [Test]
    public async Task SwitchingToCreateNew_WithSparkplugDeviceTopic_DefaultsChartNameToGroupNodeDevice()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([new ChartConfiguration { Name = "Existing Chart" }]);

        await OpenDialog(
            topic: "spBv1.0/Plant1/DDATA/Node-2/Device-1",
            jsonPath: "metrics.Metric-1",
            seriesName: "Metric-1");

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create New Chart")).Click();

        ChartNameInput().GetAttribute("value").Should().Be("Plant1 Node-2 Device-1");
    }

    [Test]
    public async Task Submit_CreateNew_WithSparkplugDeviceTopic_UsesDefaultTopicTitle()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        ChartConfiguration? captured = null;
        _mockChartStore.AddChartAsync(Arg.Any<Guid>(), Arg.Do<ChartConfiguration>(c => captured = c)).Returns(Task.CompletedTask);

        await OpenDialog(
            topic: "spBv1.0/Plant1/DDATA/Node-2/Device-1",
            jsonPath: "metrics.Metric-1",
            seriesName: "Metric-1");

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();

        captured.Should().NotBeNull();
        captured!.Name.Should().Be("Plant1 Node-2 Device-1");
        captured.Series.Should().ContainSingle().Which.DisplayName.Should().Be("Metric-1");
    }

    [Test]
    public async Task Submit_CreateNew_CallsChartStoreAddAsync()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);

        await OpenDialog(seriesName: "myField");

        ChartNameInput().Input("My Chart");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();

        await _mockChartStore.Received(1).AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>());
    }

    [Test]
    public async Task Submit_AddToExisting_CallsChartStoreUpdateAsync()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart" };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);

        await OpenDialog();

        // With 1 chart, it's auto-selected — click the submit button directly
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();

        await _mockChartStore.Received(1).UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>());
    }

    [Test]
    public async Task Submit_AddToExisting_AddsExactlyOneSeries_WithoutMutatingOriginal()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart" };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);
        ChartConfiguration? captured = null;
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Do<ChartConfiguration>(c => captured = c)).Returns(Task.CompletedTask);

        await OpenDialog();
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(existingChart.Id);
        captured.Series.Should().HaveCount(1);
        existingChart.Series.Should().BeEmpty("the live chart must not be mutated before persistence");
    }

    [Test]
    public async Task Submit_AddToExisting_WhenUpdateFails_LeavesOriginalChartUnchanged()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart" };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>())
            .Returns(Task.FromException(new InvalidOperationException("save failed")));

        await OpenDialog();

        try
        {
            _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();
        }
        catch (InvalidOperationException)
        {
            // expected: the persistence call failed
        }

        existingChart.Series.Should().BeEmpty();
    }

    private AngleSharp.Dom.IElement ChartNameInput() =>
        _dialogProvider.Find(".quick-add-chart-name input");

    private AngleSharp.Dom.IElement TimeoutInput() =>
        _dialogProvider.Find(".quick-add-timeout input");

    private AngleSharp.Dom.IElement ChecksSwitch() =>
        _dialogProvider.Find(".quick-add-timeout-row input[type='checkbox']");

    private string? ChecksSwitchLabel() =>
        _dialogProvider.FindComponent<MudSwitch<bool>>().Instance.Label;

    private AngleSharp.Dom.IElement ChartOption(string chartName) =>
        _dialogProvider.FindAll(".quick-add-chart-option")
            .Single(option => option.TextContent.Contains(chartName));

    private async Task<ChartConfiguration> CreateNewChart(string? timeoutInput = null, bool checksOff = false)
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        ChartConfiguration? captured = null;
        _mockChartStore.AddChartAsync(Arg.Any<Guid>(), Arg.Do<ChartConfiguration>(c => captured = c))
            .Returns(Task.CompletedTask);
        await OpenDialog(seriesName: "myField");

        if (checksOff)
            ChecksSwitch().Change(false);
        if (timeoutInput is not null)
            TimeoutInput().Change(timeoutInput);

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart")).Click();
        await _mockChartStore.Received(1).AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>());
        return captured!;
    }

    [TestCase("")]
    [TestCase("0")]
    public async Task SwitchingBackToAddToExisting_WithInvalidTimeout_KeepsTheAppendBlocked(string invalidTimeout)
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart", StaleTimeoutSeconds = 45 };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);

        await OpenDialog();

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create New Chart")).Click();
        TimeoutInput().Change(invalidTimeout);
        _dialogProvider.Markup.Should().Contain("Enter a timeout greater than zero seconds.");

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add to Existing Chart")).Click();

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series"))
            .GetAttribute("disabled").Should().NotBeNull("an invalid timeout cannot be saved to the chart");
    }

    [Test]
    public async Task CreateNew_ShowsStaleTimeoutAtTenSecondsByDefault()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);

        await OpenDialog();

        _dialogProvider.Markup.Should().Contain("Stale timeout (seconds)");
        ChecksSwitch().HasAttribute("checked").Should().BeTrue("staleness checks are on by default");
        ChecksSwitchLabel().Should().Be("Check for stale data");
        _dialogProvider.FindComponent<MudNumericField<double?>>().Instance.GetState(x => x.Value).Should().Be(10);
        TimeoutInput().HasAttribute("disabled").Should().BeFalse();
    }

    [Test]
    public async Task AddToExisting_ShowsTheSelectedChartsConfiguredTimeout()
    {
        _mockChartStore.GetCharts(_testConnectionId)
            .Returns([new ChartConfiguration { Name = "Existing Chart", StaleTimeoutSeconds = 45 }]);

        await OpenDialog();

        _dialogProvider.Markup.Should().Contain("Stale timeout (seconds)");
        _dialogProvider.FindComponent<MudNumericField<double?>>().Instance.GetState(x => x.Value).Should().Be(45);
        ChecksSwitch().HasAttribute("checked").Should().BeTrue();
    }

    [Test]
    public async Task AddToExisting_ShowsChecksOffWhenTheSelectedChartHasNoTimeout()
    {
        _mockChartStore.GetCharts(_testConnectionId)
            .Returns([new ChartConfiguration { Name = "On change only", StaleTimeoutSeconds = null }]);

        await OpenDialog();

        ChecksSwitch().HasAttribute("checked").Should().BeFalse();
        TimeoutInput().HasAttribute("disabled").Should().BeTrue("a chart without a timeout has nothing to edit");
    }

    [Test]
    public async Task AddToExisting_SelectingAnotherChart_ShowsThatChartsTimeout()
    {
        var first = new ChartConfiguration { Name = "First", StaleTimeoutSeconds = 45 };
        var second = new ChartConfiguration { Name = "Second", StaleTimeoutSeconds = null };
        _mockChartStore.GetCharts(_testConnectionId).Returns([first, second]);

        await OpenDialog();
        ChartOption("Second").Click();

        ChecksSwitch().HasAttribute("checked").Should().BeFalse();

        ChartOption("First").Click();

        ChecksSwitch().HasAttribute("checked").Should().BeTrue();
        TimeoutInput().HasAttribute("disabled").Should().BeFalse();
        _dialogProvider.FindComponent<MudNumericField<double?>>().Instance.GetState(x => x.Value).Should().Be(45);
    }

    [Test]
    public async Task Submit_AddToExisting_SavesAChangedTimeout()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart", StaleTimeoutSeconds = 45 };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);
        ChartConfiguration? captured = null;
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Do<ChartConfiguration>(c => captured = c))
            .Returns(Task.CompletedTask);

        await OpenDialog();
        TimeoutInput().Change("30");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();

        captured!.StaleTimeoutSeconds.Should().Be(30);
    }

    [Test]
    public async Task AddToExisting_AfterChecksTurnedOffInCreateNew_AppliesNoTimeout()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart", StaleTimeoutSeconds = 45 };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);
        ChartConfiguration? captured = null;
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Do<ChartConfiguration>(c => captured = c))
            .Returns(Task.CompletedTask);

        await OpenDialog();

        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create New Chart")).Click();
        ChecksSwitch().Change(false);
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add to Existing Chart")).Click();

        ChecksSwitch().HasAttribute("checked").Should().BeFalse("the choice must stay visible before appending");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();

        await _mockChartStore.Received(1).UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>());
        captured!.StaleTimeoutSeconds.Should().BeNull();
        captured.Series.Should().ContainSingle().Which.Topic.Should().Be("sensors/temp");
    }

    [Test]
    public async Task Submit_CreateNew_SavesConfiguredTimeout()
    {
        var chart = await CreateNewChart(timeoutInput: "30");

        chart.StaleTimeoutSeconds.Should().Be(30);
    }

    [Test]
    public async Task Submit_CreateNew_WithChecksTurnedOff_SavesNoTimeout()
    {
        var chart = await CreateNewChart(checksOff: true);

        chart.StaleTimeoutSeconds.Should().BeNull();
    }

    [Test]
    public async Task Submit_CreateNew_WithEmptyTimeout_KeepsSubmitDisabled()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        await OpenDialog(seriesName: "myField");

        TimeoutInput().Change("");

        _dialogProvider.Markup.Should().Contain("Enter a timeout greater than zero seconds.");
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Create Chart"))
            .GetAttribute("disabled").Should().NotBeNull("a timeout is required while checks are on");
    }

    [Test]
    public async Task Submit_AddToExisting_PreservesConfiguredTimeout()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart", StaleTimeoutSeconds = 45 };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);
        ChartConfiguration? captured = null;
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Do<ChartConfiguration>(c => captured = c))
            .Returns(Task.CompletedTask);

        await OpenDialog();
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();

        captured!.StaleTimeoutSeconds.Should().Be(45);
    }

    [Test]
    public async Task Submit_AddToExisting_PreservesDisabledTimeout()
    {
        var existingChart = new ChartConfiguration { Name = "Existing Chart", StaleTimeoutSeconds = null };
        _mockChartStore.GetCharts(_testConnectionId).Returns([existingChart]);
        ChartConfiguration? captured = null;
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Do<ChartConfiguration>(c => captured = c))
            .Returns(Task.CompletedTask);

        await OpenDialog();
        _dialogProvider.FindAll("button").First(b => b.TextContent.Contains("Add Series")).Click();

        captured!.StaleTimeoutSeconds.Should().BeNull();
    }

    [Test]
    public async Task Cancel_ClosesDialog()
    {
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        var dialogRef = await OpenDialog();

        _dialogProvider.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        var result = await dialogRef.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeTrue();
    }
}
