using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;
using IConfigurationManager = MqttProbe.Services.Configuration.IConfigurationManager;

namespace MqttProbe.Shared.Tests.Services.Configuration;

[TestFixture]
public class ConfigurationManagerTests
{
    private string _configPath = null!;
    private ConfigurationManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_test_{Guid.NewGuid()}.json");
        _manager = new ConfigurationManager(_configPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task Load_WhenFileDoesNotExist_CreatesNewEmptyConfiguration()
    {
        await _manager.Load();

        _manager.Configuration.Connections.Should().BeEmpty();
        File.Exists(_configPath).Should().BeTrue("Load creates the file if missing");
    }

    [Test]
    public async Task Interface_ExposesStartupLoadSaveAndSecretLoading()
    {
        IConfigurationManager manager = _manager;
        var mockStorage = Substitute.For<ISecretStorage>();
        mockStorage.GetAsync(Arg.Any<string>()).Returns((string?)null);

        await manager.Load();
        await manager.Save();
        await manager.LoadSecretsAsync(mockStorage);

        manager.Configuration.Should().NotBeNull();
    }

    [Test]
    public async Task Load_WhenFileExists_DeserializesConnections()
    {
        var json = """{"Connections":[{"Name":"Broker1","Host":"localhost","Port":1883,"Protocol":0,"ClientId":"test","WebsocketBasePath":"mqtt","UseTls":false,"AllowUntrustedCertificate":false}],"Auth":{"Username":"","PasswordHash":""}}""";
        await File.WriteAllTextAsync(_configPath, json);

        await _manager.Load();

        _manager.Configuration.Connections.Should().HaveCount(1);
        _manager.Configuration.Connections[0].Name.Should().Be("Broker1");
    }

    [Test]
    public async Task Load_WhenConfigSectionsAreNull_NormalizesToDefaults()
    {
        var json = """{"Connections":null,"Auth":null,"Performance":null}""";
        await File.WriteAllTextAsync(_configPath, json);

        await _manager.Load();

        _manager.Configuration.Connections.Should().BeEmpty();
        _manager.Configuration.Auth.Should().NotBeNull();
        _manager.Configuration.Performance.Should().NotBeNull();
        _manager.Configuration.Performance.MaxStoredMessages.Should().Be(10_000);
        _manager.Configuration.Performance.MaxMessagesPerSecond.Should().Be(50_000);
    }

    [Test]
    public async Task Load_WhenFileIsCorrupt_FallsBackToEmptyConfiguration()
    {
        await File.WriteAllTextAsync(_configPath, "not valid json {{{{");

        await _manager.Load();

        _manager.Configuration.Connections.Should().BeEmpty();
        _manager.Configuration.Auth.Should().NotBeNull();
    }

    [Test]
    public async Task Load_WhenFileDoesNotExist_ReturnsDefaultWithoutThrowing()
    {
        var act = async () => await _manager.Load();
        await act.Should().NotThrowAsync();
        _manager.Configuration.Should().NotBeNull();
        _manager.Configuration.Connections.Should().BeEmpty();
    }

    [Test]
    public async Task Save_WritesConnectionsToFile()
    {
        await _manager.Load();
        await _manager.AddConnection(new Connection { Name = "MyBroker", Host = "mqtt.local" });

        var json = await File.ReadAllTextAsync(_configPath);
        json.Should().Contain("MyBroker");
    }

    [Test]
    public async Task Save_StripsBrokerPasswords_BeforeWriting()
    {
        await _manager.Load();
        await _manager.AddConnection(new Connection { Name = "Sec", Host = "h", Password = "super-secret" });

        var json = await File.ReadAllTextAsync(_configPath);
        json.Should().NotContain("super-secret");
    }

    [Test]
    public async Task Save_WritesValidJson()
    {
        await _manager.Load();
        await _manager.AddConnection(new Connection { Name = "Test", Host = "h" });

        var json = await File.ReadAllTextAsync(_configPath);
        var act = () => System.Text.Json.JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Test]
    public async Task AddConnection_AppendsToList()
    {
        await _manager.Load();

        await _manager.AddConnection(new Connection { Name = "A", Host = "h" });
        await _manager.AddConnection(new Connection { Name = "B", Host = "h" });

        _manager.Configuration.Connections.Should().HaveCount(2);
    }

    [Test]
    public async Task AddConnection_PersistsAfterReload()
    {
        await _manager.Load();
        await _manager.AddConnection(new Connection { Name = "Persisted", Host = "h" });

        var manager2 = new ConfigurationManager(_configPath);
        await manager2.Load();

        manager2.Configuration.Connections.Should().Contain(c => c.Name == "Persisted");
    }

    [Test]
    public async Task RemoveConnection_RemovesFromList()
    {
        await _manager.Load();
        var conn = new Connection { Name = "ToRemove", Host = "h" };
        await _manager.AddConnection(conn);

        await _manager.RemoveConnection(conn);

        _manager.Configuration.Connections.Should().NotContain(c => c.Name == "ToRemove");
    }

    [Test]
    public async Task RemoveConnection_PersistsAfterReload()
    {
        await _manager.Load();
        var conn = new Connection { Name = "Ephemeral", Host = "h" };
        await _manager.AddConnection(conn);
        await _manager.RemoveConnection(conn);

        var manager2 = new ConfigurationManager(_configPath);
        await manager2.Load();

        manager2.Configuration.Connections.Should().NotContain(c => c.Name == "Ephemeral");
    }

    [Test]
    public async Task AddConnection_ReplacesExistingWithSameName()
    {
        await _manager.Load();
        await _manager.AddConnection(new Connection { Name = "Dup", Host = "old-host" });
        await _manager.AddConnection(new Connection { Name = "Dup", Host = "new-host" });

        _manager.Configuration.Connections.Should().HaveCount(1);
        _manager.Configuration.Connections[0].Host.Should().Be("new-host");
    }

    [Test]
    public async Task SetPasswordAsync_HashesAndStoresPasswordHash()
    {
        await _manager.Load();

        await _manager.SetPasswordAsync("admin", "s3cret");

        _manager.Configuration.Auth.Username.Should().Be("admin");
        _manager.Configuration.Auth.PasswordHash.Should().NotBeNullOrEmpty();
        _manager.Configuration.Auth.PasswordHash.Should().NotBe("s3cret", "should be hashed, not plain text");
    }

    [Test]
    public async Task VerifyCredentials_ReturnsTrueForCorrectPassword()
    {
        await _manager.Load();
        await _manager.SetPasswordAsync("admin", "correct");

        _manager.VerifyCredentials("admin", "correct").Should().BeTrue();
    }

    [Test]
    public async Task VerifyCredentials_ReturnsFalseForWrongPassword()
    {
        await _manager.Load();
        await _manager.SetPasswordAsync("admin", "correct");

        _manager.VerifyCredentials("admin", "wrong").Should().BeFalse();
    }

    [Test]
    public async Task LoadSecretsAsync_RestoresPasswordsFromStorage()
    {
        await _manager.Load();
        var conn = new Connection { Name = "C", Host = "h", Password = "broker-pass" };
        await _manager.AddConnection(conn);

        var mockStorage = Substitute.For<ISecretStorage>();
        mockStorage.GetAsync("mqtt_6B23C0D5F35D1B11").Returns("broker-pass");

        await _manager.LoadSecretsAsync(mockStorage);

        _manager.Configuration.Connections[0].Password.Should().Be("broker-pass");
    }

    [Test]
    public async Task LoadSecretsAsync_IgnoresMissingSecrets_DoesNotThrow()
    {
        await _manager.Load();
        await _manager.AddConnection(new Connection { Name = "NoSecret", Host = "h" });

        var mockStorage = Substitute.For<ISecretStorage>();
        mockStorage.GetAsync(Arg.Any<string>()).Returns((string?)null);

        var act = async () => await _manager.LoadSecretsAsync(mockStorage);
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task HasAdminPassword_ReturnsFalse_WhenHashIsEmpty()
    {
        await _manager.Load();

        _manager.Configuration.Auth.PasswordHash.Should().BeEmpty();
    }

    [Test]
    public async Task HasAdminPassword_ReturnsTrue_AfterSetPassword()
    {
        await _manager.Load();
        await _manager.SetPasswordAsync("admin", "pass");

        _manager.Configuration.Auth.PasswordHash.Should().NotBeEmpty();
    }

    [Test]
    public async Task AddConnection_WithEmptyPassword_RemovesSecretFromStorage()
    {
        await _manager.Load();
        var mockStorage = Substitute.For<ISecretStorage>();
        mockStorage.RemoveAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        await _manager.LoadSecretsAsync(mockStorage);
        mockStorage.ClearReceivedCalls();

        await _manager.AddConnection(new Connection { Name = "MyBroker", Host = "h", Password = "" });

        await mockStorage.Received(1).RemoveAsync("mqtt_721D663A768B79B1");
    }

    [Test]
    public async Task AddConnection_WithPassword_SetsSecretInStorage()
    {
        await _manager.Load();
        var mockStorage = Substitute.For<ISecretStorage>();
        mockStorage.SetAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        await _manager.LoadSecretsAsync(mockStorage);
        mockStorage.ClearReceivedCalls();

        await _manager.AddConnection(new Connection { Name = "Broker", Host = "h", Password = "s3cret" });

        await mockStorage.Received(1).SetAsync("mqtt_8AE618CE6C0E954A", "s3cret");
    }

    [Test]
    public async Task RemoveConnection_WithSecretStorage_RemovesSecret()
    {
        await _manager.Load();
        var mockStorage = Substitute.For<ISecretStorage>();
        mockStorage.RemoveAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        mockStorage.SetAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        await _manager.LoadSecretsAsync(mockStorage);
        var conn = new Connection { Name = "ToRemove", Host = "h" };
        await _manager.AddConnection(conn);
        mockStorage.ClearReceivedCalls();

        await _manager.RemoveConnection(conn);

        await mockStorage.Received(1).RemoveAsync("mqtt_91506C209D41E6B8");
    }

    [Test]
    public async Task IsHintDismissed_ReturnsFalse_ForUndismissedHint()
    {
        await _manager.Load();

        _manager.IsHintDismissed("publish").Should().BeFalse();
    }

    [Test]
    public async Task DismissHintAsync_MarksHintDismissed()
    {
        await _manager.Load();

        await _manager.DismissHintAsync("publish");

        _manager.IsHintDismissed("publish").Should().BeTrue();
    }

    [Test]
    public async Task DismissHintAsync_PersistsAfterReload()
    {
        await _manager.Load();
        await _manager.DismissHintAsync("emulation");

        var manager2 = new ConfigurationManager(_configPath);
        await manager2.Load();

        manager2.IsHintDismissed("emulation").Should().BeTrue();
    }

    [Test]
    public async Task DismissHintAsync_IsIdempotent_NoDuplicateEntries()
    {
        await _manager.Load();

        await _manager.DismissHintAsync("charts");
        await _manager.DismissHintAsync("charts");

        _manager.Configuration.Ui.DismissedHints.Should().ContainSingle(h => h == "charts");
    }

    [Test]
    public async Task SecretKey_SpecialCharsInName_SanitizesCorrectly()
    {
        await _manager.Load();
        var mockStorage = Substitute.For<ISecretStorage>();
        mockStorage.RemoveAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        await _manager.LoadSecretsAsync(mockStorage);
        mockStorage.ClearReceivedCalls();

        await _manager.AddConnection(new Connection { Name = "My:Broker#1", Host = "h", Password = "" });

        await mockStorage.Received(1).RemoveAsync("mqtt_CA1B513FBA72C0A3");
    }
}
