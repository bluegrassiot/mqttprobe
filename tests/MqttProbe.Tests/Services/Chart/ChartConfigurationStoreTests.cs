using Microsoft.Extensions.Logging;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Services.Chart;

[TestFixture]
public class ChartConfigurationStoreTests
{
    private string _filePath = null!;
    private ChartConfigurationStore _store = null!;
    private CapturingLogger<ChartConfigurationStore> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"charts_test_{Guid.NewGuid()}.json");
        _logger = new CapturingLogger<ChartConfigurationStore>();
        _store = new ChartConfigurationStore(_filePath, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    [Test]
    public async Task LoadAsync_FileDoesNotExist_StartsEmpty()
    {
        await _store.LoadAsync();
        _store.Configurations.Should().BeEmpty();
    }

    [Test]
    public async Task AddAsync_PersistsToFile()
    {
        await _store.LoadAsync();
        var config = new ChartConfiguration { Name = "Test Chart" };
        await _store.AddAsync(config);

        File.Exists(_filePath).Should().BeTrue();
        var store2 = new ChartConfigurationStore(_filePath);
        await store2.LoadAsync();
        store2.Configurations.Should().HaveCount(1);
        store2.Configurations[0].Name.Should().Be("Test Chart");
    }

    [Test]
    public async Task LoadAsync_RoundTrip_PreservesAllProperties()
    {
        await _store.LoadAsync();
        var config = new ChartConfiguration
        {
            Name = "My Chart",
            Type = ChartType.Area,
            MaxPoints = 250,
            TimeWindowMinutes = 10,
            Series =
            [
                new ChartSeries
                {
                    DisplayName = "Temperature",
                    Topic = "sensor/room",
                    JsonPath = "temperature",
                    Color = "#FF5722"
                }
            ]
        };
        await _store.AddAsync(config);

        var store2 = new ChartConfigurationStore(_filePath);
        await store2.LoadAsync();
        var loaded = store2.Configurations[0];
        loaded.Name.Should().Be("My Chart");
        loaded.Type.Should().Be(ChartType.Area);
        loaded.MaxPoints.Should().Be(250);
        loaded.TimeWindowMinutes.Should().Be(10);
        loaded.Series.Should().HaveCount(1);
        loaded.Series[0].DisplayName.Should().Be("Temperature");
        loaded.Series[0].Topic.Should().Be("sensor/room");
        loaded.Series[0].JsonPath.Should().Be("temperature");
        loaded.Series[0].Color.Should().Be("#FF5722");
    }

    [Test]
    public async Task UpdateAsync_UpdatesExistingConfig()
    {
        await _store.LoadAsync();
        var config = new ChartConfiguration { Name = "Original" };
        await _store.AddAsync(config);

        config.Name = "Updated";
        await _store.UpdateAsync(config);

        var store2 = new ChartConfigurationStore(_filePath);
        await store2.LoadAsync();
        store2.Configurations.Should().HaveCount(1);
        store2.Configurations[0].Name.Should().Be("Updated");
    }

    [Test]
    public async Task RemoveAsync_RemovesConfigById()
    {
        await _store.LoadAsync();
        var config1 = new ChartConfiguration { Name = "Chart 1" };
        var config2 = new ChartConfiguration { Name = "Chart 2" };
        await _store.AddAsync(config1);
        await _store.AddAsync(config2);

        await _store.RemoveAsync(config1.Id);

        _store.Configurations.Should().HaveCount(1);
        _store.Configurations[0].Name.Should().Be("Chart 2");
    }

    [Test]
    public async Task Configurations_ReturnsStableSnapshotAfterMutations()
    {
        await _store.LoadAsync();
        await _store.AddAsync(new ChartConfiguration { Name = "Chart 1" });
        var snapshot = _store.Configurations;

        await _store.AddAsync(new ChartConfiguration { Name = "Chart 2" });

        snapshot.Should().HaveCount(1);
        snapshot[0].Name.Should().Be("Chart 1");
        _store.Configurations.Should().HaveCount(2);
    }

    [Test]
    public async Task Configurations_RemainsReadableWhileStoreMutates()
    {
        await _store.LoadAsync();
        await _store.AddAsync(new ChartConfiguration { Name = "Initial" });

        var readers = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => _store.Configurations.Select(c => c.Name).ToList()));
        var writers = Enumerable.Range(0, 50)
            .Select(i => _store.AddAsync(new ChartConfiguration { Name = $"Chart {i}" }));

        var act = async () => await Task.WhenAll(readers.Concat(writers));

        await act.Should().NotThrowAsync();
        _store.Configurations.Should().HaveCount(51);
    }

    [Test]
    public async Task LoadAsync_CorruptFile_StartsEmpty()
    {
        await File.WriteAllTextAsync(_filePath, "not valid json {{{{");
        await _store.LoadAsync();
        _store.Configurations.Should().BeEmpty();
    }

    [Test]
    public async Task LoadAsync_CorruptFile_LogsWarningAndStartsEmpty()
    {
        await File.WriteAllTextAsync(_filePath, "not valid json {{{{");

        await _store.LoadAsync();

        _store.Configurations.Should().BeEmpty();
        _logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Failed to load chart configurations", StringComparison.Ordinal) &&
            entry.Exception != null);
    }

    [Test]
    public async Task LoadAsync_EmptyFile_StartsEmpty()
    {
        await File.WriteAllTextAsync(_filePath, "");
        await _store.LoadAsync();
        _store.Configurations.Should().BeEmpty();
    }

    [Test]
    public async Task AddAsync_FiresConfigurationsChanged()
    {
        var fired = false;
        _store.ConfigurationsChanged += () => fired = true;

        await _store.AddAsync(new ChartConfiguration { Name = "X" });

        fired.Should().BeTrue();
    }

    [Test]
    public async Task UpdateAsync_FiresConfigurationsChanged()
    {
        var config = new ChartConfiguration { Name = "Original" };
        await _store.AddAsync(config);

        var fired = false;
        _store.ConfigurationsChanged += () => fired = true;

        config.Name = "Updated";
        await _store.UpdateAsync(config);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task RemoveAsync_FiresConfigurationsChanged()
    {
        var config = new ChartConfiguration { Name = "ToRemove" };
        await _store.AddAsync(config);

        var fired = false;
        _store.ConfigurationsChanged += () => fired = true;

        await _store.RemoveAsync(config.Id);

        fired.Should().BeTrue();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
