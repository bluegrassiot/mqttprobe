using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Configuration;

[TestFixture]
public class ConnectionSettingsTests
{
    private string _configPath = null!;
    private SettingsDocument _document = null!;
    private ConnectionSettings _connections = null!;

    [SetUp]
    public void Setup()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"mqttprobe_settings_{Guid.NewGuid()}.json");
        if (File.Exists(_configPath)) File.Delete(_configPath);
        _document = new SettingsDocument(_configPath);
        _connections = new ConnectionSettings(_document, new ConnectionSecrets(null, null), null);
    }

    [TearDown]
    public void TearDown()
    {
        _document.Dispose();
        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Test]
    public async Task AddConnectionAsync_SameIdDifferentName_ReplacesNotDuplicates()
    {
        await new SettingsLoader(_document, new ConnectionSecrets(null, null), false, null).LoadAsync();
        var conn = new Connection { Name = "Original", Host = "h", Port = 1883 };
        await _connections.AddConnectionAsync(conn);

        var renamed = conn.Clone();
        renamed.Name = "Renamed";
        await _connections.AddConnectionAsync(renamed);

        _document.Config.Connections.Should().Contain(c => c.Name == "Renamed");
        _document.Config.Connections.Should().NotContain(c => c.Name == "Original");
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

    // The document must NOT fail here: the throw comes from secret storage, not the save.
    // This covers the path where the old secret was already removed (oldKey != newKey) before
    // the new secret write fails, which must still mark secretsMutated so the rollback runs.
    [Test]
    public async Task AddConnectionAsync_RenameSecretSetFailure_RestoresPasswordUnderOldKey()
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
        storage.SetAsync(newKey, "secret")
            .Returns(_ => Task.FromException(new InvalidOperationException("keychain locked")));

        storage.ClearReceivedCalls();

        var act = () => connections.AddConnectionAsync(renamed);
        await act.Should().ThrowAsync<InvalidOperationException>();

        document.Config.Connections.Should().Contain(c => c.Name == "Original");
        document.Config.Connections.Should().NotContain(c => c.Name == "Renamed");
        await storage.Received().RemoveAsync(oldKey);
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
