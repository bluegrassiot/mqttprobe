using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MudBlazor;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

[TestFixture]
public class SubscriptionManagerTests
{
    private IMqttManagedClient _mockClient = null!;
    private ILogger<SubscriptionManager> _mockLogger = null!;
    private ISnackbar _mockSnackbar = null!;
    private ISettingsStore _mockSettingsStore = null!;
    private ISessionState _mockSessionState = null!;
    private SubscriptionManager _manager = null!;

    private Func<MqttClientConnectedEventArgs, Task>? _connectedHandler;
    private Func<MqttManagedProcessFailedEventArgs, Task>? _syncFailedHandler;

    [SetUp]
    public void Setup()
    {
        _mockClient = Substitute.For<IMqttManagedClient>();
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
            .When(x => x.SynchronizingSubscriptionsFailedAsync += Arg.Any<Func<MqttManagedProcessFailedEventArgs, Task>>())
            .Do(x => _syncFailedHandler = x.Arg<Func<MqttManagedProcessFailedEventArgs, Task>>());

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
    public void Constructor_InitializesWithEmptySubscriptions()
    {
        _manager.Subscriptions.Should().BeEmpty();
    }

    [Test]
    public async Task Add_CallsManagedClientSubscribeAsync()
    {
        await _manager.Add("test/topic");

        await _mockClient.Received(1).SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
    }

    [Test]
    public async Task Add_AddsTopicToActiveSubscriptions()
    {
        await _manager.Add("test/topic");

        _manager.Subscriptions.Should().Contain(s => s.Topic == "test/topic");
    }

    [Test]
    public async Task Add_MultipleTopics_AllAdded()
    {
        await _manager.Add("a/b");
        await _manager.Add("c/d");
        await _manager.Add("e/f");

        _manager.Subscriptions.Select(s => s.Topic).Should().BeEquivalentTo(["a/b", "c/d", "e/f"]);
    }

    [Test]
    public async Task Remove_CallsManagedClientUnsubscribeAsync()
    {
        await _manager.Add("test/topic");

        await _manager.Remove(["test/topic"]);

        await _mockClient.Received(1).UnsubscribeAsync(Arg.Any<IEnumerable<string>>());
    }

    [Test]
    public async Task Remove_RemovesTopicFromActiveSubscriptions()
    {
        await _manager.Add("test/topic");

        await _manager.Remove(["test/topic"]);

        _manager.Subscriptions.Should().NotContain(s => s.Topic == "test/topic");
    }

    [Test]
    public async Task Remove_NonExistentTopic_DoesNotThrow()
    {
        var act = async () => await _manager.Remove(["does/not/exist"]);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Subscriptions_ReturnsStableReadOnlySnapshot()
    {
        var snapshot = _manager.Subscriptions;

        await _manager.Add("snapshot/topic");

        snapshot.Should().BeEmpty();
        _manager.Subscriptions.Should().Contain(s => s.Topic == "snapshot/topic");
    }

    [TestCase(MqttQualityOfServiceLevel.AtMostOnce)]
    [TestCase(MqttQualityOfServiceLevel.AtLeastOnce)]
    [TestCase(MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Add_UsesRequestedQos_InTopicFilter(MqttQualityOfServiceLevel qos)
    {
        await _manager.Add("qos/test", qos);

        await _mockClient.Received(1).SubscribeAsync(
            Arg.Is<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>(filters =>
                filters!.Any(f => f.Topic == "qos/test" && f.QualityOfServiceLevel == qos)));
    }

    [Test]
    public async Task Add_DefaultQos_IsAtLeastOnce()
    {
        await _manager.Add("default/qos");

        await _mockClient.Received(1).SubscribeAsync(
            Arg.Is<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>(filters =>
                filters!.Any(f => f.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce)));
    }

    [Test]
    public async Task Add_DuplicateTopic_DoesNotResubscribe()
    {
        await _manager.Add("dup/topic", MqttQualityOfServiceLevel.AtLeastOnce);
        _mockClient.ClearReceivedCalls();
        _mockSnackbar.ClearReceivedCalls();

        await _manager.Add("dup/topic", MqttQualityOfServiceLevel.ExactlyOnce);

        await _mockClient.DidNotReceive().SubscribeAsync(Arg.Any<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>());
        _manager.Subscriptions.Should().ContainSingle(s =>
            s.Topic == "dup/topic" && s.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtLeastOnce);
        _mockSnackbar.Received(1).Add(
            Arg.Is<string>(m => m!.Contains("Already subscribed", StringComparison.OrdinalIgnoreCase)),
            Severity.Warning,
            Arg.Any<Action<SnackbarOptions>?>(),
            Arg.Any<string?>());
    }

    [Test]
    public async Task Add_PersistsTopicAndQos()
    {
        await _manager.Add("test/topic", MqttQualityOfServiceLevel.ExactlyOnce);

        await _mockSettingsStore.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(c =>
                c!.SubscribedTopics.Any(s =>
                    s.Topic == "test/topic" &&
                    s.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)));
    }

    [Test]
    public async Task Add_ThenRemove_LeavesEmptySubscriptions()
    {
        await _manager.Add("test/topic");
        await _manager.Remove(["test/topic"]);

        _manager.Subscriptions.Should().BeEmpty();
    }

    [Test]
    public async Task ConcurrentAddRemove_LeavesSubscriptionsReadable()
    {
        var addTasks = Enumerable.Range(0, 100)
            .Select(i => _manager.Add($"topic/{i}"));
        await Task.WhenAll(addTasks);

        var readers = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => _manager.Subscriptions.ToList()));
        var removers = Enumerable.Range(0, 100)
            .Select(i => _manager.Remove([$"topic/{i}"]));

        var act = async () => await Task.WhenAll(readers.Concat(removers));

        await act.Should().NotThrowAsync();
        _manager.Subscriptions.Should().BeEmpty();
    }

    [Test]
    public async Task OnConnected_WithTopics_ResubscribesAll()
    {
        _connectedHandler.Should().NotBeNull("component should subscribe to ConnectedAsync in constructor");

        await _manager.Add("a/b");
        await _manager.Add("c/d");
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

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
    public async Task OnConnected_ResubscribesWithStoredQos()
    {
        await _manager.Add("a/b", MqttQualityOfServiceLevel.AtMostOnce);
        await _manager.Add("c/d", MqttQualityOfServiceLevel.ExactlyOnce);
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        await _mockClient.Received(1).SubscribeAsync(
            Arg.Is<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>(filters =>
                filters!.Any(f => f.Topic == "a/b" && f.QualityOfServiceLevel == MqttQualityOfServiceLevel.AtMostOnce) &&
                filters!.Any(f => f.Topic == "c/d" && f.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)));
    }

    [Test]
    public async Task OnConnected_WithAutoResubscribe_LoadsSavedQos()
    {
        var connection = new Connection
        {
            Name = "Test",
            Host = "localhost",
            SubscribedTopics =
            [
                new() { Topic = "saved/topic1", QualityOfServiceLevel = MqttQualityOfServiceLevel.ExactlyOnce },
                new() { Topic = "saved/topic2", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce }
            ]
        };
        _mockSessionState.SelectedConnection.Returns(connection);
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Subscriptions.Should().Contain(s =>
            s.Topic == "saved/topic1" && s.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce);
        await _mockClient.Received(1).SubscribeAsync(
            Arg.Is<IEnumerable<MQTTnet.Packets.MqttTopicFilter>>(filters =>
                filters!.Any(f =>
                    f.Topic == "saved/topic1" &&
                    f.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)));
    }

    [Test]
    public async Task OnConnected_Merge_DoesNotOverwriteInMemoryQos()
    {
        await _manager.Add("shared/topic", MqttQualityOfServiceLevel.ExactlyOnce);

        var connection = new Connection
        {
            Name = "Test",
            Host = "localhost",
            SubscribedTopics =
            [
                new() { Topic = "shared/topic", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce },
                new() { Topic = "saved/only", QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce }
            ]
        };
        _mockSessionState.SelectedConnection.Returns(connection);
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Subscriptions.Should().Contain(s =>
            s.Topic == "shared/topic" && s.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce);
        _manager.Subscriptions.Should().Contain(s => s.Topic == "saved/only");
    }

    [Test]
    public async Task OnSyncFailed_ShowsErrorSnackbar()
    {
        _syncFailedHandler.Should().NotBeNull("component should subscribe to SynchronizingSubscriptionsFailedAsync in constructor");

        await _syncFailedHandler!(new MqttManagedProcessFailedEventArgs(new Exception("oops")));

        _mockSnackbar.Received(1).Add(Arg.Any<string>(), Severity.Error, Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Add_AtMaxSubscriptions_RejectsNewTopic()
    {
        for (var i = 0; i < 500; i++)
            await _manager.Add($"topic/{i}");

        _mockClient.ClearReceivedCalls();
        _mockSnackbar.ClearReceivedCalls();

        await _manager.Add("one/too/many");

        _manager.Subscriptions.Should().NotContain(s => s.Topic == "one/too/many");
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
            Arg.Is<Connection>(c => c!.SubscribedTopics!.Any(s => s.Topic == "test/topic")));
    }

    [Test]
    public async Task Remove_PersistsTopicsToConnection()
    {
        await _manager.Add("a/b");
        await _manager.Add("c/d");
        _mockSettingsStore.ClearReceivedCalls();

        await _manager.Remove(["a/b"]);

        await _mockSettingsStore.Received(1).AddConnectionAsync(
            Arg.Is<Connection>(c => c!.SubscribedTopics!.Any(s => s.Topic == "c/d") && !c.SubscribedTopics.Any(s => s.Topic == "a/b")));
    }

    [Test]
    public async Task OnConnected_WithAutoResubscribe_LoadsSavedTopics()
    {
        var connection = new Connection
        {
            Name = "Test",
            Host = "localhost",
            SubscribedTopics = [new SubscribedTopic { Topic = "saved/topic1" }, new SubscribedTopic { Topic = "saved/topic2" }]
        };
        _mockSessionState.SelectedConnection.Returns(connection);

        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Subscriptions.Should().Contain(s => s.Topic == "saved/topic1");
        _manager.Subscriptions.Should().Contain(s => s.Topic == "saved/topic2");
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
            SubscribedTopics = [new SubscribedTopic { Topic = "saved/topic1" }]
        };
        _mockSessionState.SelectedConnection.Returns(connection);

        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Subscriptions.Should().NotContain(s => s.Topic == "saved/topic1");
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
            SubscribedTopics = [new SubscribedTopic { Topic = "saved/topic" }]
        };
        _mockSessionState.SelectedConnection.Returns(connection);

        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Subscriptions.Should().Contain(s => s.Topic == "memory/topic");
        _manager.Subscriptions.Should().Contain(s => s.Topic == "saved/topic");
    }

    [Test]
    public async Task ClearActiveSubscriptions_WithTopics_ClearsTopics()
    {
        await _manager.Add("a/b");
        await _manager.Add("c/d");
        _mockSettingsStore.ClearReceivedCalls();

        _manager.ClearActiveSubscriptions();

        _manager.Subscriptions.Should().BeEmpty();
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

        await _mockSettingsStore.DidNotReceive().AddConnectionAsync(Arg.Any<Connection>());
    }

    [Test]
    public async Task ClearActiveSubscriptions_DoesNotCallUnsubscribe()
    {
        await _manager.Add("a/b");
        _mockClient.ClearReceivedCalls();

        _manager.ClearActiveSubscriptions();

        await _mockClient.DidNotReceive().UnsubscribeAsync(Arg.Any<IEnumerable<string>>());
    }

    [Test]
    public async Task ClearActiveSubscriptions_SetsTopicsEmpty_BeforeNextConnect()
    {
        await _manager.Add("old/topic");

        _manager.ClearActiveSubscriptions();

        var newConnection = new Connection
        {
            Name = "New",
            Host = "new-broker",
            SubscribedTopics = [new SubscribedTopic { Topic = "new/topic" }]
        };
        _mockSessionState.SelectedConnection.Returns(newConnection);
        _mockClient.ClearReceivedCalls();

        await _connectedHandler!(null!);

        _manager.Subscriptions.Should().Contain(s => s.Topic == "new/topic");
        _manager.Subscriptions.Should().NotContain(s => s.Topic == "old/topic");
    }
}
