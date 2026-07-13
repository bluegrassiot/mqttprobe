using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

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

    [Test]
    public async Task AddConnectionAsync_SameIdDifferentName_ReplacesNotDuplicates()
    {
        await _store.LoadAsync();
        var conn = new Connection { Name = "Original", Host = "h", Port = 1883 };
        await _store.AddConnectionAsync(conn);

        var renamed = conn.Clone();
        renamed.Name = "Renamed";
        await _store.AddConnectionAsync(renamed);

        _store.Config.Connections.Should().Contain(c => c.Name == "Renamed");
        _store.Config.Connections.Should().NotContain(c => c.Name == "Original");
    }

    [Test]
    public async Task AddConnectionAsync_SaveFailure_RollsBackConnectionsAndSecrets()
    {
        var store = new FailingSaveSettingsStore(_configPath);
        await store.LoadAsync();

        var initialCount = store.Config.Connections.Count;
        var conn = new Connection { Name = "Test", Host = "h", Port = 1883, Password = "pw" };

        store.EnableSaveFailure();
        var act = () => store.AddConnectionAsync(conn);
        await act.Should().ThrowAsync<IOException>();

        store.Config.Connections.Should().HaveCount(initialCount);
        store.Config.Connections.Should().NotContain(c => c.Name == "Test");
    }

    [Test]
    public async Task AddConnectionAsync_RenameSaveFailure_RestoresPasswordUnderCorrectKey()
    {
        var mockSecretStorage = Substitute.For<ISecretStorage>();
        var store = new FailingSaveSettingsStore(_configPath);
        await store.LoadAsync(mockSecretStorage);

        var conn = new Connection { Name = "Original", Host = "h", Port = 1883, Password = "secret" };
        await store.AddConnectionAsync(conn);
        store.Config.Connections.Should().Contain(c => c.Name == "Original");

        static string ExpectedSecretKey(string name)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name));
            return $"mqtt_{Convert.ToHexString(hash)[..16]}";
        }

        var oldKey = ExpectedSecretKey("Original");
        var newKey = ExpectedSecretKey("Renamed");

        var renamed = conn.Clone();
        renamed.Name = "Renamed";
        store.EnableSaveFailure();

        var act = () => store.AddConnectionAsync(renamed);
        await act.Should().ThrowAsync<IOException>();

        store.Config.Connections.Should().Contain(c => c.Name == "Original");
        store.Config.Connections.Should().NotContain(c => c.Name == "Renamed");

        await mockSecretStorage.Received().RemoveAsync(newKey);
        await mockSecretStorage.Received().SetAsync(oldKey, "secret");
    }

    [Test]
    public async Task RemoveConnectionAsync_SaveFailure_RollsBackAllMutations()
    {
        var store = new FailingSaveSettingsStore(_configPath);
        await store.LoadAsync();

        var conn = new Connection { Name = "ToDelete", Host = "h", Port = 1883, Password = "pw" };
        await store.AddConnectionAsync(conn);
        store.Config.Connections.Should().Contain(c => c.Name == "ToDelete");

        store.EnableSaveFailure();

        var act = () => store.RemoveConnectionAsync(conn);
        await act.Should().ThrowAsync<IOException>();

        store.Config.Connections.Should().Contain(c => c.Name == "ToDelete");
    }

    /// <summary>
    /// Test subclass that forces SaveCoreAsync to fail, enabling deterministic
    /// rollback testing without relying on ISecretStorage side effects.
    /// </summary>
    private class FailingSaveSettingsStore : SettingsStore
    {
        private bool _failSave;

        public FailingSaveSettingsStore(string configPath)
            : base(configPath) { }

        public void EnableSaveFailure() => _failSave = true;
        public void DisableSaveFailure() => _failSave = false;

        protected override async Task SaveCoreAsync()
        {
            if (_failSave)
                throw new IOException("disk full");
            await base.SaveCoreAsync();
        }
    }
}
