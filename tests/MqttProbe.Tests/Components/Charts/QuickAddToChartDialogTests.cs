using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Charts;

[TestFixture]
public class QuickAddToChartDialogTests : BunitTestContext
{
    private ISettingsStore _mockChartStore = null!;
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;
    private static readonly Guid _testConnectionId = Guid.NewGuid();

    [SetUp]
    public void SetupMocks()
    {
        var mockSessionState = Substitute.For<ISessionState>();
        mockSessionState.SelectedConnection.Returns(new Connection { Id = _testConnectionId });

        _mockChartStore = Substitute.For<ISettingsStore>();
        _mockChartStore.GetCharts(_testConnectionId).Returns([]);
        _mockChartStore.AddChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        _mockChartStore.UpdateChartAsync(Arg.Any<Guid>(), Arg.Any<ChartConfiguration>()).Returns(Task.CompletedTask);
        Services.AddSingleton(_mockChartStore);
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

        var chartNameInput = _dialogProvider.FindAll("input").Last();
        chartNameInput.GetAttribute("value").Should().Be("Plant1 Node-2 Device-1");
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

        var chartNameInput = _dialogProvider.FindAll("input").Last();
        chartNameInput.GetAttribute("value").Should().Be("Plant1 Node-2 Device-1");
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

        // Type a chart name to enable submit
        var inputs = _dialogProvider.FindAll("input");
        inputs[inputs.Count - 1].Input("My Chart");
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
