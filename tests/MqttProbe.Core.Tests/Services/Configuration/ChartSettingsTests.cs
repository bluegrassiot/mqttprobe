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

    [Test]
    public void NewChart_DefaultsStaleTimeoutToTenSeconds()
    {
        new ChartConfiguration().StaleTimeoutSeconds.Should().Be(10);
    }

    [Test]
    public async Task AddChartAsync_PersistsCustomStaleTimeout()
    {
        var connectionId = Guid.NewGuid();

        await _charts.AddChartAsync(connectionId, new ChartConfiguration
        {
            Name = "custom",
            StaleTimeoutSeconds = 2.5
        });

        using var reloaded = new SettingsDocument(_configPath);
        var loaded = await reloaded.ReadAsync();

        loaded!.ChartsByConnection[connectionId].Single().StaleTimeoutSeconds.Should().Be(2.5);
    }

    [Test]
    public async Task AddChartAsync_PersistsOffAsNull()
    {
        var connectionId = Guid.NewGuid();

        await _charts.AddChartAsync(connectionId, new ChartConfiguration
        {
            Name = "off",
            StaleTimeoutSeconds = null
        });

        using var reloaded = new SettingsDocument(_configPath);
        var loaded = await reloaded.ReadAsync();

        loaded!.ChartsByConnection[connectionId].Single().StaleTimeoutSeconds.Should().BeNull();
    }

    [Test]
    public async Task LegacyChartWithoutStaleTimeout_LoadsWithTenSecondDefault()
    {
        var connectionId = Guid.NewGuid();
        await File.WriteAllTextAsync(_configPath,
            $$$"""
            {"chartsByConnection":{"{{{connectionId}}}":[{"name":"legacy","type":"Line","maxPoints":100}]}}
            """);

        var loaded = await _document.ReadAsync();

        loaded!.ChartsByConnection[connectionId].Single().StaleTimeoutSeconds.Should().Be(10);
    }
}
