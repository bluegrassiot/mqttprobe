using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.Core.Tests.Services.Configuration;

[TestFixture]
public class SettingsDocumentConcurrencyTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_settings_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _document = new SettingsDocument(_configPath);
    }

    [TearDown]
    public void TearDown()
    {
        _document.Dispose();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task PerformanceSetter_ConcurrentWithAddConnection_BothChangesPresent()
    {
        var preferences = new PreferenceSettings(_document);
        var connections = new ConnectionSettings(_document, new ConnectionSecrets(null, null), null);
        await new SettingsLoader(_document, new ConnectionSecrets(null, null), false, null).LoadAsync();

        await Task.WhenAll(
            preferences.SetMaxStoredMessagesAsync(9999),
            connections.AddConnectionAsync(new Connection { Name = "ConcurrentConn", Host = "h" }));

        _document.Config.Performance.MaxStoredMessages.Should().Be(9999);
        _document.Config.Connections.Should().Contain(c => c.Name == "ConcurrentConn");
    }
}
