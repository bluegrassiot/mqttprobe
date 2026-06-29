using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class SubscriptionManagerTests
{
    private IManagedMqttClient _mockClient = null!;
    private ILogger<SubscriptionManager> _mockLogger = null!;
    private ISnackbar _mockSnackbar = null!;
    private ISettingsStore _mockSettingsStore = null!;
    private ISessionState _mockSessionState = null!;
    private SubscriptionManager _manager = null!;

    private Func<MqttClientConnectedEventArgs, Task>? _connectedHandler;
    private Func<ManagedProcessFailedEventArgs, Task>? _syncFailedHandler;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IManagedMqttClient>();
        _mockLogger = Substitute.For<ILogger<SubscriptionManager>>();
        _mockSnackbar = Substitute.For<ISnackbar>();
        _mockSettingsStore = Substitute.For<ISettingsStore>();
        _mockSessionState = Substitute.For<ISessionState>();

        var config = new AppConfiguration { Ui = new UiPreferences { AutoResubscribe = true } };
        _mockSettingsStore.Config.Returns(config);
        _mockSessionState.SelectedConnection.Returns(new Connection { Name = "Test", Host = "localhost" });

        _connectedHandler = null;
        _syncFailedHandler = null;
        _mockClient
            .When(x => x.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(x => _connectedHandler = x.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        _mockClient
            .When(x => x.SynchronizingSubscriptionsFailedAsync += Arg.Any<Func<ManagedProcessFailedEventArgs, Task>>())
            .Do(x => _syncFailedHandler = x.Arg<Func<ManagedProcessFailedEventArgs, Task>>());

        _manager = new SubscriptionManager(_mockClient, _mockLogger, _mockSnackbar, _mockSettingsStore, _mockSessionState);
    }

    [TearDown]
    public void TearDown()
    {
        _mockClient.Dispose();
        _mockSnackbar.Dispose();
        _manager.Dispose();
    }

    [Test]
    public void Constructor_InitializesWithEmptyTopicSet()
    {
        _manager.Topics.Should().BeEmpty();
    }

    [Test]
    public async Task Add_CallsManagedClientSubscribeAsync()
    {
        await _manager.Add("test/topic");

        await _mockClient.Received(1).SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
    }

    [Test]
    public async Task Add_AddsTopicToActiveTopics()
    {
        await _manager.Add("test/topic");

        _manager.Topics.Should().Contain("test/topic");
    }

    [Test]
    public async Task Add_MultipleTopics_AllAdded()
    {
        await _manager.Add("a/b");
        await _manager.Add("c/d");
        await _manager.Add("e/f");

        _manager.Topics.Should().HaveCount(3).And.Contain(["a/b", "c/d", "e/f"]);
    }

    [Test]
    public async Task Remove_CallsManagedClientUnsubscribeAsync()
    {
        await _manager.Add("test/topic");

        await _manager.Remove(["test/topic"]);

        await _mockClient.Received(1).UnsubscribeAsync(Arg.Any<IEnumerable<string>>());
    }

    [Test]
    public async Task Remove_RemovesTopicFromActiveTopics()
    {
        await _manager.Add("test/topic");

        await _manager.Remove(["test/topic"]);

        _manager.Topics.Should().NotContain("test/topic");
    }

    [Test]
    public async Task Remove_NonExistentTopic_DoesNotThrow()
    {
        var act = async () => await _manager.Remove(["does/not/exist"]);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Topics_ReturnsStableReadOnlySnapshot()
    {
        var snapshot = _manager.Topics;

        await _manager.Add("snapshot/topic");

        snapshot.Should().BeEmpty();
        _manager.Topics.Should().Contain("snapshot/topic");
    }

    [Test]
    public async Task Add_UsesMqttQosAtLeastOnce_InTopicFilter()
    {
        await _manager.Add("qos/test");

        await _mockClient.Received(1).SubscribeAsync(
            Arg.Is<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>(filters =>
                filters.Any(f => f.QualityOfServiceLevel == MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)));
    }

    [Test]
    public async Task Add_ThenRemove_LeavesEmptyTopicSet()
    {
        await _manager.Add("test/topic");
        await _manager.Remove(["test/topic"]);

        _manager.Topics.Should().BeEmpty();
    }

    [Test]
    public async Task ConcurrentAddRemove_LeavesTopicSnapshotReadable()
    {
        var addTasks = Enumerable.Range(0, 100)
            .Select(i => _manager.Add($"topic/{i}"));
        await Task.WhenAll(addTasks);

        var readers = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => _manager.Topics.ToList()));
        var removers = Enumerable.Range(0, 100)
            .Select(i => _manager.Remove([$"topic/{i}"]));

        var act = async () => await Task.WhenAll(readers.Concat(removers));

        await act.Should().NotThrowAsync();
        _manager.Topics.Should().BeEmpty();
    }


    [Test]
    public async Task OnConnected_WithTopics_ResubscribesAll()
    {
        _connectedHandler.Should().NotBeNull("component should subscribe to ConnectedAsync in constructor");

        await _manager.Add("a/b");
        await _manager.Add("c/d");
        // 2 SubscribeAsync calls from Add; reset to count only the reconnect ones
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        // One batched SubscribeAsync call for all topics
        await _mockClient.Received(1).SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
    }

    [Test]
    public async Task OnConnected_WithNoTopics_DoesNotResubscribe()
    {
        _connectedHandler.Should().NotBeNull();
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        await _mockClient.DidNotReceive().SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
    }

    [Test]
    public async Task OnSyncFailed_ShowsErrorSnackbar()
    {
        _syncFailedHandler.Should().NotBeNull("component should subscribe to SynchronizingSubscriptionsFailedAsync in constructor");

        await _syncFailedHandler!(new ManagedProcessFailedEventArgs(new Exception("oops"), [], []));

        _mockSnackbar.Received(1).Add(Arg.Any<string>(), Severity.Error, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Add_AtMaxSubscriptions_RejectsNewTopic()
    {
        // Fill to the limit
        for (var i = 0; i < 500; i++)
            await _manager.Add($"topic/{i}");

        _mockClient.ClearReceivedCalls();
        _mockSnackbar.ClearReceivedCalls();

        await _manager.Add("one/too/many");

        _manager.Topics.Should().NotContain("one/too/many");
        _mockSnackbar.Received(1).Add(Arg.Any<string>(), Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
        await _mockClient.DidNotReceive().SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
    }

    [Test]
    public async Task Add_InvalidTopic_NullChar_ShowsWarning()
    {
        await _manager.Add("valid\0topic");

        await _mockClient.DidNotReceive().SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
        _mockSnackbar.Received(1).Add(Arg.Any<string>(), Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Add_InvalidTopic_TooLong_ShowsWarning()
    {
        await _manager.Add(new string('a', 65_536));

        await _mockClient.DidNotReceive().SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
        _mockSnackbar.Received(1).Add(Arg.Any<string>(), Severity.Warning, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public void Dispose_UnregistersEventHandlers()
    {
        bool connectedUnsubscribed = false;
        _mockClient
            .When(x => x.ConnectedAsync -= Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(_ => connectedUnsubscribed = true);

        _manager.Dispose();

        connectedUnsubscribed.Should().BeTrue("Dispose must unregister the ConnectedAsync handler");
    }

    [Test]
    public async Task Add_PersistsTopicsToConnection()
    {
        await _manager.Add("test/topic");

        await _mockSettingsStore.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(c => c.SubscribedTopics.Contains("test/topic")));
    }

    [Test]
    public async Task Remove_PersistsTopicsToConnection()
    {
        await _manager.Add("a/b");
        await _manager.Add("c/d");
        _mockSettingsStore.ClearReceivedCalls();

        await _manager.Remove(["a/b"]);

        await _mockSettingsStore.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(c => c.SubscribedTopics.Contains("c/d") && !c.SubscribedTopics.Contains("a/b")));
    }

    [Test]
    public async Task OnConnected_WithAutoResubscribe_LoadsSavedTopics()
    {
        var connection = new Connection
        {
            Name = "Test",
            Host = "localhost",
            SubscribedTopics = ["saved/topic1", "saved/topic2"]
        };
        _mockSessionState.SelectedConnection.Returns(connection);

        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Topics.Should().Contain("saved/topic1");
        _manager.Topics.Should().Contain("saved/topic2");
        await _mockClient.Received(1).SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
    }

    [Test]
    public async Task OnConnected_WithoutAutoResubscribe_DoesNotLoadSavedTopics()
    {
        var config = new AppConfiguration { Ui = new UiPreferences { AutoResubscribe = false } };
        _mockSettingsStore.Config.Returns(config);

        var connection = new Connection
        {
            Name = "Test",
            Host = "localhost",
            SubscribedTopics = ["saved/topic1"]
        };
        _mockSessionState.SelectedConnection.Returns(connection);

        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Topics.Should().NotContain("saved/topic1");
        await _mockClient.DidNotReceive().SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
    }

    [Test]
    public async Task OnConnected_MergesSavedTopicsWithInMemoryTopics()
    {
        await _manager.Add("memory/topic");

        var connection = new Connection
        {
            Name = "Test",
            Host = "localhost",
            SubscribedTopics = ["saved/topic"]
        };
        _mockSessionState.SelectedConnection.Returns(connection);

        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Topics.Should().Contain("memory/topic");
        _manager.Topics.Should().Contain("saved/topic");
    }

    [Test]
    public async Task ClearActiveSubscriptions_WithTopics_ClearsTopics()
    {
        await _manager.Add("a/b");
        await _manager.Add("c/d");
        _mockSettingsStore.ClearReceivedCalls();

        _manager.ClearActiveSubscriptions();

        _manager.Topics.Should().BeEmpty();
    }

    [Test]
    public void ClearActiveSubscriptions_WhenEmpty_DoesNotThrow()
    {
        var act = () => _manager.ClearActiveSubscriptions();

        act.Should().NotThrow();
    }

    [Test]
    public async Task ClearActiveSubscriptions_DoesNotPersistToConnection()
    {
        await _manager.Add("a/b");
        _mockSettingsStore.ClearReceivedCalls();

        _manager.ClearActiveSubscriptions();

        // ClearActiveSubscriptions must NOT call AddConnectionAsync —
        // it is a runtime-only clear, not a user action.
        await _mockSettingsStore.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task ClearActiveSubscriptions_DoesNotCallUnsubscribe()
    {
        await _manager.Add("a/b");
        _mockClient.ClearReceivedCalls();

        _manager.ClearActiveSubscriptions();

        // No MQTT unsubscribe — broker may already be disconnected.
        await _mockClient.DidNotReceive().UnsubscribeAsync(Arg.Any<IEnumerable<string>>());
    }

    [Test]
    public async Task ClearActiveSubscriptions_SetsTopicsEmpty_BeforeNextConnect()
    {
        await _manager.Add("old/topic");

        _manager.ClearActiveSubscriptions();

        // After clear, simulating a new connect with auto-resubscribe should
        // only load the new connection's saved topics, not the old ones.
        var newConnection = new Connection
        {
            Name = "New",
            Host = "new-broker",
            SubscribedTopics = ["new/topic"]
        };
        _mockSessionState.SelectedConnection.Returns(newConnection);
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Topics.Should().Contain("new/topic");
        _manager.Topics.Should().NotContain("old/topic");
    }
}
