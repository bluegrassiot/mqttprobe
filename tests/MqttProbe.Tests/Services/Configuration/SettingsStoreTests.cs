using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Emulation;
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
        _store.Dispose();
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
    public async Task SetAutoRequestSparkplugRebirthAsync_PersistsAndRaisesUiPreferencesChanged()
    {
        var fired = false;
        _store.UiPreferencesChanged += () => fired = true;
        _store.Config.Ui.AutoRequestSparkplugRebirth.Should().BeFalse("auto-rebirth ships off");

        await _store.SetAutoRequestSparkplugRebirthAsync(true);

        fired.Should().BeTrue();

        using var reloaded = new SettingsStore(_configPath);
        await reloaded.LoadAsync();
        reloaded.Config.Ui.AutoRequestSparkplugRebirth.Should().BeTrue();
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
    public async Task SetPasswordAsync_RoundTripsThroughVerifyCredentials()
    {
        await _store.SetPasswordAsync("admin", "correct-horse");

        _store.VerifyCredentials("admin", "correct-horse").Should().BeTrue();
        _store.VerifyCredentials("admin", "wrong").Should().BeFalse();
        _store.VerifyCredentials("someone-else", "correct-horse").Should().BeFalse();

        using var reloaded = new SettingsStore(_configPath);
        await reloaded.LoadAsync();
        reloaded.VerifyCredentials("admin", "correct-horse").Should().BeTrue();
    }

    // Chart and emulator ops live on collaborators; SettingsStore forwards their events.
    // Without explicit add/remove accessors that forwarding compiles clean and never fires.
    [Test]
    public async Task AddChartAsync_RaisesChartsChangedForTheConnection()
    {
        var connectionId = Guid.NewGuid();
        Guid? raisedFor = null;
        _store.ChartsChanged += id => raisedFor = id;

        await _store.AddChartAsync(connectionId, new ChartConfiguration { Name = "c" });

        raisedFor.Should().Be(connectionId);
        _store.GetCharts(connectionId).Should().ContainSingle(c => c.Name == "c");
    }

    [Test]
    public async Task AddEmulatorNodeAsync_RaisesEmulatorsChangedForTheConnection()
    {
        var connectionId = Guid.NewGuid();
        Guid? raisedFor = null;
        _store.EmulatorsChanged += id => raisedFor = id;

        await _store.AddEmulatorNodeAsync(connectionId, new EmulatorNodeConfig { NodeId = "n" });

        raisedFor.Should().Be(connectionId);
        _store.GetEmulatorNodes(connectionId).Should().ContainSingle(n => n.NodeId == "n");
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
            .AllSatisfy(c => c.SubscribedTopics.Should().Contain(s => s.Topic == "spBv1.0/#"));
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

    // The pre-multi-connection "charts"/"emulators" keys were deleted from AppConfiguration.
    // Deserialization must keep skipping unknown keys: a parse failure here is swallowed by
    // LoadOrCreateConfigAsync, which would silently reset an existing user's whole config.
    [Test]
    public async Task LoadAsync_WhenFileHasLegacyTopLevelKeys_StillLoadsTheRest()
    {
        await File.WriteAllTextAsync(_configPath,
            """
            {"connections":[{"name":"Kept","host":"h","port":1883}],
             "charts":[{"name":"old-chart"}],
             "emulators":{"publishIntervalMs":250,"nodes":[]}}
            """);

        var loaded = await _store.LoadAsync();

        loaded.Should().BeTrue("an unknown key must not fail the parse");
        _store.Connections.Should().ContainSingle(c => c.Name == "Kept");
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

    // A document whose save can be armed to fail, making rollback deterministic without
    // relying on ISecretStorage side effects.
    private sealed class FailingDocument(AppConfiguration config) : ISettingsDocument
    {
        private bool _failSave;

        public AppConfiguration Config { get; } = config;

        public void EnableSaveFailure() => _failSave = true;

        public Task SaveAsync() =>
            _failSave ? Task.FromException(new IOException("disk full")) : Task.CompletedTask;

        public Task ExclusiveAsync(Func<Task> action) => action();

        public Task MutateAndSaveAsync(Action<AppConfiguration> mutate)
        {
            mutate(Config);
            return SaveAsync();
        }
    }

    [Test]
    public async Task AddConnectionAsync_SaveFailure_RollsBackConnections()
    {
        var document = new FailingDocument(new AppConfiguration());
        var connections = new ConnectionSettings(document, new ConnectionSecrets(null, null), null);
        var initialCount = document.Config.Connections.Count;

        document.EnableSaveFailure();
        var act = () => connections.AddConnectionAsync(
            new Connection { Name = "Test", Host = "h", Port = 1883, Password = "pw" });
        await act.Should().ThrowAsync<IOException>();

        document.Config.Connections.Should().HaveCount(initialCount);
        document.Config.Connections.Should().NotContain(c => c.Name == "Test");
    }

    [Test]
    public async Task AddConnectionAsync_RenameSaveFailure_RestoresPasswordUnderCorrectKey()
    {
        var storage = Substitute.For<ISecretStorage>();
        var document = new FailingDocument(new AppConfiguration());
        var connections = new ConnectionSettings(document, new ConnectionSecrets(storage, null), null);

        var conn = new Connection { Name = "Original", Host = "h", Port = 1883, Password = "secret" };
        await connections.AddConnectionAsync(conn);
        document.Config.Connections.Should().Contain(c => c.Name == "Original");

        var oldKey = ConnectionSecrets.KeyFor(conn);
        var renamed = conn.Clone();
        renamed.Name = "Renamed";
        var newKey = ConnectionSecrets.KeyFor(renamed);
        storage.GetAsync(oldKey).Returns("secret");

        // Without this the assertions below are satisfied by the first, successful
        // AddConnectionAsync and cannot discriminate the rollback at all. The pre-existing
        // version of this test had that flaw.
        storage.ClearReceivedCalls();

        document.EnableSaveFailure();
        var act = () => connections.AddConnectionAsync(renamed);
        await act.Should().ThrowAsync<IOException>();

        document.Config.Connections.Should().Contain(c => c.Name == "Original");
        document.Config.Connections.Should().NotContain(c => c.Name == "Renamed");
        await storage.Received().RemoveAsync(newKey);
        await storage.Received().SetAsync(oldKey, "secret");
    }

    [Test]
    public async Task RemoveConnectionAsync_SaveFailure_RollsBackAllMutations()
    {
        var document = new FailingDocument(new AppConfiguration());
        var connections = new ConnectionSettings(document, new ConnectionSecrets(null, null), null);

        var conn = new Connection { Name = "ToDelete", Host = "h", Port = 1883, Password = "pw" };
        await connections.AddConnectionAsync(conn);
        document.Config.Connections.Should().Contain(c => c.Name == "ToDelete");

        document.EnableSaveFailure();
        var act = () => connections.RemoveConnectionAsync(conn);
        await act.Should().ThrowAsync<IOException>();

        document.Config.Connections.Should().Contain(c => c.Name == "ToDelete");
    }
}
