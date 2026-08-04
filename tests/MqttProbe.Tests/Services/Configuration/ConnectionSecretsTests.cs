using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Configuration;

[TestFixture]
public class ConnectionSecretsTests
{
    private static Connection Conn(string name = "Broker", string? password = null) =>
        new() { Name = name, Host = "h", Port = 1883, Password = password };

    [Test]
    public void KeyFor_DerivesFromNameOnly_AndIsStable()
    {
        var key = ConnectionSecrets.KeyFor(Conn());

        key.Should().Be(ConnectionSecrets.KeyFor(Conn()));
        key.Should().NotBe(ConnectionSecrets.KeyFor(Conn("Other")));
        key.Should().StartWith("mqtt_").And.HaveLength(21, "prefix plus 16 hex chars");
    }

    [Test]
    public async Task WithoutStorage_IsDisabledAndEveryOperationNoOps()
    {
        var secrets = new ConnectionSecrets(null, null);

        secrets.IsEnabled.Should().BeFalse();
        (await secrets.GetAsync(Conn())).Should().BeNull();

        var act = async () =>
        {
            await secrets.SetAsync(Conn("Broker", "pw"));
            await secrets.RemoveAsync("mqtt_whatever");
            await secrets.RestoreAfterUpsertFailureAsync(Conn(), "mqtt_old", "old-pw");
            await secrets.RestoreAfterRemoveFailureAsync(Conn(), "pw");
        };
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task SetAsync_WithEmptyPassword_RemovesInsteadOfStoring()
    {
        var storage = Substitute.For<ISecretStorage>();
        var secrets = new ConnectionSecrets(storage, null);
        var connection = Conn("Broker", "");

        await secrets.SetAsync(connection);

        await storage.Received().RemoveAsync(ConnectionSecrets.KeyFor(connection));
        await storage.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task RestoreAfterUpsertFailure_RemovingNewKeyThrows_StillRestoresOldKey()
    {
        var storage = Substitute.For<ISecretStorage>();
        var attempted = Conn("Renamed");
        storage.RemoveAsync(ConnectionSecrets.KeyFor(attempted))
            .Returns(Task.FromException(new IOException("boom")));
        var secrets = new ConnectionSecrets(storage, null);

        await secrets.RestoreAfterUpsertFailureAsync(attempted, "mqtt_old", "old-pw");

        await storage.Received().SetAsync("mqtt_old", "old-pw");
    }

    [Test]
    public async Task RestoreAfterUpsertFailure_WithNoPreviousPassword_RemovesTheOldKey()
    {
        var storage = Substitute.For<ISecretStorage>();
        var secrets = new ConnectionSecrets(storage, null);

        await secrets.RestoreAfterUpsertFailureAsync(Conn("Renamed"), "mqtt_old", null);

        await storage.Received().RemoveAsync("mqtt_old");
    }

    [Test]
    public async Task RestoreAfterUpsertFailure_RestoringOldKeyThrows_DoesNotPropagate()
    {
        var storage = Substitute.For<ISecretStorage>();
        storage.SetAsync("mqtt_old", "old-pw").Returns(Task.FromException(new IOException("boom")));
        var secrets = new ConnectionSecrets(storage, null);

        var act = () => secrets.RestoreAfterUpsertFailureAsync(Conn("Renamed"), "mqtt_old", "old-pw");

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task RestoreAfterRemoveFailure_WritesSecretBackUnderOwnKey()
    {
        var storage = Substitute.For<ISecretStorage>();
        var secrets = new ConnectionSecrets(storage, null);
        var removed = Conn("Removed");

        await secrets.RestoreAfterRemoveFailureAsync(removed, "pw");

        await storage.Received().SetAsync(ConnectionSecrets.KeyFor(removed), "pw");
    }

    [Test]
    public async Task RestoreAfterRemoveFailure_WithNullValue_WritesNothing()
    {
        var storage = Substitute.For<ISecretStorage>();
        var secrets = new ConnectionSecrets(storage, null);

        await secrets.RestoreAfterRemoveFailureAsync(Conn("Removed"), null);

        await storage.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task RestoreAfterRemoveFailure_SetAsyncThrows_DoesNotPropagate()
    {
        var storage = Substitute.For<ISecretStorage>();
        var removed = Conn("Removed");
        storage.SetAsync(ConnectionSecrets.KeyFor(removed), "pw")
            .Returns(Task.FromException(new IOException("boom")));
        var secrets = new ConnectionSecrets(storage, null);

        var act = () => secrets.RestoreAfterRemoveFailureAsync(removed, "pw");

        await act.Should().NotThrowAsync();
    }
}
