using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Chart;
using MqttProbe.Shared.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Components.Charts;

[TestFixture]
public class ChartFieldPickerTests
{
    // Tests for GetShortLabel — the method that derives a human-readable name from a JSON path

    [TestCase("temperature", ExpectedResult = "temperature", TestName = "Flat field returns itself")]
    [TestCase("metrics.temperature", ExpectedResult = "temperature", TestName = "SparkplugB metrics prefix stripped")]
    [TestCase("metrics.Battery Voltage", ExpectedResult = "Battery Voltage", TestName = "SparkplugB metric with spaces stripped")]
    [TestCase("sensors.indoor.temp", ExpectedResult = "temp", TestName = "Deeply nested returns last segment")]
    [TestCase("a.b.c.d", ExpectedResult = "d", TestName = "Multi-level nesting returns leaf")]
    [TestCase("readings[0]", ExpectedResult = "readings[0]", TestName = "Indexed array with no dot returns full path")]
    [TestCase("data.values[2]", ExpectedResult = "values[2]", TestName = "Array under object returns array segment")]
    public string GetShortLabel_ReturnsExpected(string jsonPath)
        => ChartFieldPicker.GetShortLabel(jsonPath);

    // FormatValue

    [TestCase(0.0, ExpectedResult = "0", TestName = "Zero formats as integer")]
    [TestCase(42.0, ExpectedResult = "42", TestName = "Whole number formats without decimals")]
    [TestCase(3.14159, ExpectedResult = "3.14159", TestName = "Small decimal uses G6")]
    [TestCase(1_000_000.0, ExpectedResult = "1E+06", TestName = "Large number uses G4")]
    [TestCase(0.0001, ExpectedResult = "0.0001", TestName = "Small positive decimal at boundary")]
    [TestCase(0.00001, ExpectedResult = "1E-05", TestName = "Very small number uses scientific notation")]
    public string FormatValue_ReturnsExpected(double value)
        => ChartFieldPicker.FormatValue(value);

    // RowStyle

    [Test]
    public void RowStyle_Selected_ReturnsHighlightedStyle()
    {
        var style = ChartFieldPicker.RowStyle(true);
        style.Should().Contain("mud-palette-primary");
    }

    [Test]
    public void RowStyle_NotSelected_ReturnsTransparentBorder()
    {
        var style = ChartFieldPicker.RowStyle(false);
        style.Should().Contain("transparent");
        style.Should().NotContain("mud-palette-primary-lighten");
    }
}

[TestFixture]
public class ChartFieldPickerRenderTests : BunitTestContext
{
    private IRenderedComponent<MudDialogProvider> _dialogProvider = null!;

    [SetUp]
    public void Setup()
    {
        var registry = Substitute.For<IChartFieldRegistry>();
        registry.GetTopics().Returns([]);
        Services.AddSingleton(registry);

        EnsureMudProviders();
        _dialogProvider = Render<MudDialogProvider>();
    }

    private async Task OpenPicker()
    {
        var dialogService = Services.GetRequiredService<IDialogService>();
        await _dialogProvider.InvokeAsync(() => dialogService.ShowAsync<ChartFieldPicker>("Add Series",
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true }));
    }

    [Test]
    public async Task SearchText_IsIsolatedBetweenConcurrentInstances()
    {
        await OpenPicker();
        await OpenPicker();

        _dialogProvider.FindAll("input")[0].Input("uniquesearch");

        var values = _dialogProvider.FindAll("input")
            .Select(i => i.GetAttribute("value") ?? string.Empty)
            .ToList();
        values.Count(v => v == "uniquesearch").Should().Be(1);
        values.Count(string.IsNullOrEmpty).Should().Be(1);
    }

    [Test]
    public async Task SearchText_DoesNotPersistWhenDialogReopened()
    {
        await OpenPicker();
        _dialogProvider.Find("input").Input("staletext");
        _dialogProvider.Find("input").GetAttribute("value").Should().Be("staletext");

        _dialogProvider.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
        await OpenPicker();

        _dialogProvider.Find("input").GetAttribute("value").Should().BeNullOrEmpty();
    }
}
