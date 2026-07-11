using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;

namespace MqttProbe.Shared.Tests.Services.Configuration;

[TestFixture]
public class SettingsStoreTests
{
    private string _configPath = null!;
    private SettingsStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_settings_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _store = new SettingsStore(_configPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task SetThemeAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.SetThemeAsync("light");

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetFontAccessibleAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.SetFontAccessibleAsync(true);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetFontFamilyAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.SetFontFamilyAsync("Roboto");

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetAutoResubscribeAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.SetAutoResubscribeAsync(false);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task DismissHintAsync_RaisesUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.DismissHintAsync("any-hint");

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetMaxStoredMessagesAsync_PersistsValue()
    {
        await _store.SetMaxStoredMessagesAsync(5000);

        _store.Config.Performance.MaxStoredMessages.Should().Be(5000);
    }

    [Test]
    public async Task SetMaxStoredMessagesAsync_RaisesPerformanceSettingsChanged()
    {
        var fired = false;
        _store.PerformanceSettingsChanged += () => fired = true;

        await _store.SetMaxStoredMessagesAsync(5000);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetMaxStoredMessagesAsync_DoesNotRaiseUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.SetMaxStoredMessagesAsync(5000);

        fired.Should().BeFalse("performance setters must not raise the UI event");
    }

    [Test]
    public async Task SetMaxMessagesPerSecondAsync_DoesNotRaiseUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.SetMaxMessagesPerSecondAsync(2000);

        fired.Should().BeFalse("performance setters must not raise the UI event");
    }

    [Test]
    public async Task SetMaxMessagesPerSecondAsync_PersistsValue()
    {
        await _store.SetMaxMessagesPerSecondAsync(2000);

        _store.Config.Performance.MaxMessagesPerSecond.Should().Be(2000);
    }

    [Test]
    public async Task SetMaxMessagesPerSecondAsync_RaisesPerformanceSettingsChanged()
    {
        var fired = false;
        _store.PerformanceSettingsChanged += () => fired = true;

        await _store.SetMaxMessagesPerSecondAsync(2000);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetMaxStoredMessagesAsync_ConcurrentWithAddConnection_BothChangesPresent()
    {
        await _store.LoadAsync();

        await Task.WhenAll(
            _store.SetMaxStoredMessagesAsync(9999),
            _store.AddConnectionAsync(new Connection { Name = "ConcurrentConn", Host = "h" }));

        _store.Config.Performance.MaxStoredMessages.Should().Be(9999);
        _store.Config.Connections.Should().Contain(c => c.Name == "ConcurrentConn");
    }

    [Test]
    public async Task SetMaxDisplayMessagesAsync_PersistsValue()
    {
        await _store.SetMaxDisplayMessagesAsync(300);

        _store.Config.Performance.MaxDisplayMessages.Should().Be(300);
    }

    [Test]
    public async Task SetMaxDisplayMessagesAsync_RaisesPerformanceSettingsChanged()
    {
        var fired = false;
        _store.PerformanceSettingsChanged += () => fired = true;

        await _store.SetMaxDisplayMessagesAsync(300);

        fired.Should().BeTrue();
    }

    [Test]
    public async Task SetMaxDisplayMessagesAsync_DoesNotRaiseUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;

        await _store.SetMaxDisplayMessagesAsync(300);

        fired.Should().BeFalse("performance setters must not raise the UI event");
    }

    [Test]
    public async Task SetMaxTopicNodesAsync_Roundtrips()
    {
        await _store.SetMaxTopicNodesAsync(5000);

        _store.Config.Performance.MaxTopicNodes.Should().Be(5000);
    }

    [Test]
    public async Task SetMaxTopicNodesAsync_RejectsValuesBelow100()
    {
        await _store.SetMaxTopicNodesAsync(5000);
        await _store.SetMaxTopicNodesAsync(50);

        _store.Config.Performance.MaxTopicNodes.Should().Be(5000);
    }

    [Test]
    public async Task LoadAsync_WhenFileDoesNotExist_SeedsThePickedPublicBrokers()
    {
        await _store.LoadAsync();

        var conns = _store.Config.Connections;
        conns.Should().HaveCount(3);
        conns.Should().Contain(c =>
            c.Host == "broker.hivemq.com" && c.Port == 1883 &&
            c.Protocol == Protocol.Mqtt && !c.UseTls);
        conns.Should().Contain(c =>
            c.Host == "test.mosquitto.org" && c.Port == 8081 &&
            c.Protocol == Protocol.WebSocket && c.UseTls &&
            !c.AllowUntrustedCertificate && c.WebsocketBasePath == "mqtt");
        conns.Should().Contain(c =>
            c.Host == "broker.emqx.io" && c.Port == 8883 &&
            c.Protocol == Protocol.Mqtt && c.UseTls && !c.AllowUntrustedCertificate);
    }

    [Test]
    public async Task LoadAsync_WhenFileDoesNotExist_SeededBrokersSubscribeToSparkplugAndHaveUniqueClientIds()
    {
        await _store.LoadAsync();

        _store.Config.Connections.Should()
            .AllSatisfy(c => c.SubscribedTopics.Should().Contain("spBv1.0/#"));
        var clientIds = _store.Config.Connections.Select(c => c.ClientId).ToList();
        clientIds.Should().OnlyHaveUniqueItems("shared client IDs evict each other on public brokers");
        clientIds.Should().AllSatisfy(id => id.Should().StartWith("mqttprobe_"));
    }

    [Test]
    public async Task LoadAsync_PersistsSeededBrokersSoSecondLoadDoesNotReSeed()
    {
        await _store.LoadAsync();

        var store2 = new SettingsStore(_configPath);
        await store2.LoadAsync();

        store2.Config.Connections.Should().HaveCount(3, "seeding happens only when the file is first created");
    }

    [Test]
    public async Task LoadAsync_WhenFileExistsWithNoConnections_DoesNotSeed()
    {
        await File.WriteAllTextAsync(_configPath,
            """{"connections":[],"auth":{"username":"","passwordHash":""}}""");

        await _store.LoadAsync();

        _store.Config.Connections.Should().BeEmpty("an existing config is never reseeded");
    }
}
