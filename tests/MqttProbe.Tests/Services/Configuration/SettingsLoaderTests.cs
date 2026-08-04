using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Configuration;

[TestFixture]
public class SettingsLoaderTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;
    private SettingsLoader _loader = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_settings_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _document = new SettingsDocument(_configPath);
        _loader = new SettingsLoader(_document, new ConnectionSecrets(null, null), false, null);
    }

    [TearDown]
    public void TearDown()
    {
        _document.Dispose();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task LoadAsync_WhenFileDoesNotExist_SeedsThePickedPublicBrokers()
    {
        await _loader.LoadAsync();

        var conns = _document.Config.Connections;
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
        await _loader.LoadAsync();

        _document.Config.Connections.Should()
            .AllSatisfy(c => c.SubscribedTopics.Should().Contain(s => s.Topic == "spBv1.0/#"));
        var clientIds = _document.Config.Connections.Select(c => c.ClientId).ToList();
        clientIds.Should().OnlyHaveUniqueItems("shared client IDs evict each other on public brokers");
        clientIds.Should().AllSatisfy(id => id.Should().StartWith("mqttprobe_"));
    }

    [Test]
    public async Task LoadAsync_PersistsSeededBrokersSoSecondLoadDoesNotReSeed()
    {
        await _loader.LoadAsync();

        using var document2 = new SettingsDocument(_configPath);
        await new SettingsLoader(document2, new ConnectionSecrets(null, null), false, null).LoadAsync();

        document2.Config.Connections.Should().HaveCount(3, "seeding happens only when the file is first created");
    }

    [Test]
    public async Task LoadAsync_WhenFileExistsWithNoConnections_DoesNotSeed()
    {
        await File.WriteAllTextAsync(_configPath,
            """{"connections":[],"auth":{"username":"","passwordHash":""}}""");

        await _loader.LoadAsync();

        _document.Config.Connections.Should().BeEmpty("an existing config is never reseeded");
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

        var loaded = await _loader.LoadAsync();

        loaded.Should().BeTrue("an unknown key must not fail the parse");
        _document.Config.Connections.Should().ContainSingle(c => c.Name == "Kept");
    }

    [Test]
    public async Task LoadAsync_WhenMobileAndFileDoesNotExist_SeedsReducedPerformanceLimits()
    {
        var loader = new SettingsLoader(_document, new ConnectionSecrets(null, null), isMobile: true, null);

        await loader.LoadAsync();

        // MaxDisplayMessages is excluded: its default is already 500, so it cannot discriminate.
        _document.Config.Performance.MaxStoredMessages.Should().Be(1_000);
        _document.Config.Performance.MaxMessagesPerSecond.Should().Be(1_000);
        _document.Config.Performance.MaxTopicNodes.Should().Be(1_000);
    }

    [Test]
    public async Task LoadAsync_WhenNotMobile_LeavesPerformanceDefaultsAlone()
    {
        var loader = new SettingsLoader(_document, new ConnectionSecrets(null, null), isMobile: false, null);

        await loader.LoadAsync();

        _document.Config.Performance.MaxStoredMessages.Should().Be(10_000);
        _document.Config.Performance.MaxMessagesPerSecond.Should().Be(50_000);
        _document.Config.Performance.MaxTopicNodes.Should().Be(10_000);
    }

    // The loop body in LoadSecretsAsync (connection.Password = stored) is what restores saved
    // broker passwords at startup on every host; every other test here wires a no-op
    // ConnectionSecrets, so this is the only coverage of that assignment actually running.
    [Test]
    public async Task LoadAsync_WhenSecretStorageHasAStoredPassword_PopulatesConnectionPassword()
    {
        var connection = new Connection { Name = "Stored", Host = "h", Port = 1883 };
        _document.Config = new AppConfiguration { Connections = [connection] };
        await _document.SaveAsync();

        var key = ConnectionSecrets.KeyFor(connection);
        var storage = Substitute.For<ISecretStorage>();
        storage.GetAsync(key).Returns("s3cr3t");

        using var document2 = new SettingsDocument(_configPath);
        var loader = new SettingsLoader(document2, new ConnectionSecrets(storage, null), false, null);

        await loader.LoadAsync();

        document2.Config.Connections.Should().ContainSingle(c => c.Name == "Stored" && c.Password == "s3cr3t");
    }
}
