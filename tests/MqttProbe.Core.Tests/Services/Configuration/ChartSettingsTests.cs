using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Tests.Services.Configuration;

[TestFixture]
public class ChartSettingsTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;
    private ChartSettings _charts = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_settings_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _document = new SettingsDocument(_configPath);
        _charts = new ChartSettings(_document);
    }

    [TearDown]
    public void TearDown()
    {
        _document.Dispose();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task AddChartAsync_RaisesChartsChangedForTheConnection()
    {
        var connectionId = Guid.NewGuid();
        Guid? raisedFor = null;
        _charts.ChartsChanged += id => raisedFor = id;

        await _charts.AddChartAsync(connectionId, new ChartConfiguration { Name = "c" });

        raisedFor.Should().Be(connectionId);
        _charts.GetCharts(connectionId).Should().ContainSingle(c => c.Name == "c");
    }
}
