using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientConnectionLifetimeTests
{
    private static MqttManagedClientOptions BuildOptions(TimeSpan? reconnect = null) => new()
    {
        ClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("test")
            .Build(),
        AutoReconnectDelay = reconnect ?? TimeSpan.FromMinutes(5)
    };

    private static MqttClientConnectedEventArgs ConnectedArgs() => new(new MqttClientConnectResult());

    private static MqttApplicationMessage Message(string topic) =>
        new MqttApplicationMessageBuilder().WithTopic(topic).Build();

    private static MqttClientPublishResult PublishResult() => new(null, default, null!, []);

    private static MqttClientSubscribeResult SubscribeResult(MqttClientSubscribeOptions options) => new(
        1,
        options.TopicFilters.Select(filter => new MqttClientSubscribeResultItem(
            filter, MqttClientSubscribeResultCode.GrantedQoS0)).ToArray(),
        null!,
        []);

    private static MqttClientUnsubscribeResult UnsubscribeResult(MqttClientUnsubscribeOptions options) => new(
        1,
        options.TopicFilters.Select(topic => new MqttClientUnsubscribeResultItem(
            topic, MqttClientUnsubscribeResultCode.Success)).ToArray(),
        null!,
        []);

    private static MqttClientUnsubscribeResult UnsubscribeResult(string topic) => new(
        1,
        [new MqttClientUnsubscribeResultItem(topic, MqttClientUnsubscribeResultCode.Success)],
        null!,
        []);

    private static MqttTopicFilter Filter(string topic) => new MqttTopicFilterBuilder().WithTopic(topic).Build();

    [Test]
    public async Task RawReconnect_ChangesConnectionEpochWithoutChangingAttemptId()
    {
        var connectCount = 0;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                if (Interlocked.Increment(ref connectCount) > 1)
                    reconnected.TrySetResult();
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(10)));
        var outcome = await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        outcome.Status.Should().Be(MqttConnectStatus.Connected);
        var firstEpoch = sut.ConnectionEpoch;

        client.IsConnected.Returns(false);
        await disconnected.Handler!(DisconnectedArgs());
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sut.ConnectionEpoch.Should().NotBe(firstEpoch);
        attempt.Id.Should().Be(outcome.AttemptId);
    }

    [Test]
    public async Task RawReconnect_WaitsForAdmittedConnectedPublishToDrain()
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectCount = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref connectCount) > 1)
                    secondConnectStarted.TrySetResult();
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                publishStarted.TrySetResult();
                await releasePublish.Task;
                return PublishResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(10)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        var publish = sut.TryPublishConnectedAsync(Message("old-epoch"), sut.ConnectionEpoch);
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        client.IsConnected.Returns(false);
        await disconnected.Handler!(DisconnectedArgs());
        await Task.Delay(100);
        secondConnectStarted.Task.IsCompleted.Should().BeFalse();

        releasePublish.TrySetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(5));
        await secondConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task PartialSubscribeAck_ConfirmsOnlyGrantedFiltersAndRejectsStaleRevisionPublish()
    {
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var filters = call.Arg<MqttClientSubscribeOptions>().TopicFilters;
                var items = filters.Select((filter, index) => new MqttClientSubscribeResultItem(
                    filter,
                    index == 0
                        ? MqttClientSubscribeResultCode.GrantedQoS1
                        : MqttClientSubscribeResultCode.NotAuthorized)).ToArray();
                return Task.FromResult(new MqttClientSubscribeResult(1, items, null!, []));
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await FluentActions.Invoking(() => sut.SubscribeAsync([Filter("accepted"), Filter("rejected")]))
            .Should().ThrowAsync<InvalidOperationException>();

        var snapshot = sut.LiveSubscriptionSnapshot ?? throw new InvalidOperationException("Expected confirmed subscriptions.");
        snapshot.ConfirmedFilters.Should().Equal("accepted");
        var rejectedRevision = snapshot.Revision;

        await sut.SubscribeAsync([Filter("third")]);
        var stalePublish = await sut.TryPublishConnectedAsync(
            Message("stale-revision"), snapshot.ConnectionEpoch, rejectedRevision, CancellationToken.None);
        stalePublish.Should().BeFalse();
        await client.DidNotReceive().PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnsubscribeInvalidatesFilterBeforeConcurrentSubscribeAcknowledgement()
    {
        var unsubscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUnsubscribe = new TaskCompletionSource<MqttClientUnsubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(SubscribeResult(call.Arg<MqttClientSubscribeOptions>())));
        client.UnsubscribeAsync(Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<MqttClientUnsubscribeOptions>().TopicFilters.Should().ContainSingle("B");
                unsubscribeStarted.TrySetResult();
                return releaseUnsubscribe.Task;
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await sut.SubscribeAsync([Filter("B")]);

        var unsubscribe = sut.UnsubscribeAsync(["B"]);
        await unsubscribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.SubscribeAsync([Filter("C")]);

        var snapshot = sut.LiveSubscriptionSnapshot ?? throw new InvalidOperationException("Expected confirmed subscriptions.");
        snapshot.ConfirmedFilters.Should().Contain("C").And.NotContain("B");
        releaseUnsubscribe.SetResult(UnsubscribeResult("B"));
        await unsubscribe;
        sut.LiveSubscriptionSnapshot!.ConfirmedFilters.Should().NotContain("B");
    }

    [Test]
    public async Task SameFilterResubscribeWaitsForOlderUnsubscribeBrokerEffect()
    {
        var unsubscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUnsubscribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSubscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var brokerSubscriptions = new HashSet<string>(StringComparer.Ordinal);
        var subscribeCount = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var filter in call.Arg<MqttClientSubscribeOptions>().TopicFilters)
                    brokerSubscriptions.Add(filter.Topic);
                if (Interlocked.Increment(ref subscribeCount) == 2)
                    secondSubscribeStarted.TrySetResult();
                return Task.FromResult(SubscribeResult(call.Arg<MqttClientSubscribeOptions>()));
            });
        client.UnsubscribeAsync(Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                call.Arg<MqttClientUnsubscribeOptions>().TopicFilters.Should().ContainSingle("B");
                unsubscribeStarted.TrySetResult();
                await releaseUnsubscribe.Task;
                foreach (var topic in call.Arg<MqttClientUnsubscribeOptions>().TopicFilters)
                    brokerSubscriptions.Remove(topic);
                return UnsubscribeResult(call.Arg<MqttClientUnsubscribeOptions>());
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await sut.SubscribeAsync([Filter("B")]);

        var unsubscribe = sut.UnsubscribeAsync(["B"]);
        await unsubscribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var resubscribe = sut.SubscribeAsync([Filter("B")]);
        resubscribe.IsCompleted.Should().BeFalse();
        subscribeCount.Should().Be(1);

        releaseUnsubscribe.SetResult();
        await unsubscribe.WaitAsync(TimeSpan.FromSeconds(5));
        await secondSubscribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await resubscribe.WaitAsync(TimeSpan.FromSeconds(5));

        brokerSubscriptions.Should().ContainSingle("B");
        sut.LiveSubscriptionSnapshot!.ConfirmedFilters.Should().ContainSingle("B");
    }

    [Test]
    public async Task FailedUnsubscribeCanBeRetriedWhenDesiredFilterIsAlreadyAbsent()
    {
        var unsubscribeCount = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });
        client.UnsubscribeAsync(Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => Interlocked.Increment(ref unsubscribeCount) == 1
                ? Task.FromException<MqttClientUnsubscribeResult>(new InvalidOperationException("transport failure"))
                : Task.FromResult(UnsubscribeResult(call.Arg<MqttClientUnsubscribeOptions>())));

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await FluentActions.Invoking(() => sut.UnsubscribeAsync(["B"]))
            .Should().ThrowAsync<InvalidOperationException>();
        await sut.UnsubscribeAsync(["B"]);

        unsubscribeCount.Should().Be(2);
        var snapshot = sut.LiveSubscriptionSnapshot ?? throw new InvalidOperationException("Expected confirmed snapshot.");
        snapshot.ConfirmedFilters.Should().NotContain("B");
    }

    [Test]
    public async Task PausedInboundHandlerKeepsCapturedEpochWhileLiveEpochAdvances()
    {
        var connectedCount = 0;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rawClient = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(rawClient);
        var disconnected = CaptureDisconnected(rawClient);
        var received = CaptureMessage(rawClient);
        rawClient.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                rawClient.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                if (Interlocked.Increment(ref connectedCount) > 1)
                    reconnected.TrySetResult();
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(rawClient);
        await using var sut = new MqttManagedClient(factory, null);
        long? capturedEpoch = null;
        long? resumedEpoch = null;
        sut.ApplicationMessageReceivedAsync += async _ =>
        {
            capturedEpoch = sut.InboundConnectionEpoch;
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
            resumedEpoch = sut.InboundConnectionEpoch;
        };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(10)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        var oldEpoch = sut.ConnectionEpoch;
        var inbound = received.Handler!(new MqttApplicationMessageReceivedEventArgs(
            "test-client", Message("inbound"), new MqttPublishPacket { Topic = "inbound" }, null!));
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        rawClient.IsConnected.Returns(false);
        await disconnected.Handler!(DisconnectedArgs());
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sut.ConnectionEpoch.Should().NotBe(oldEpoch);

        releaseHandler.TrySetResult();
        await inbound.WaitAsync(TimeSpan.FromSeconds(5));
        capturedEpoch.Should().Be(oldEpoch);
        resumedEpoch.Should().Be(oldEpoch);
    }

    private static ConnectedCapture CaptureConnected(IMqttClient client)
    {
        var capture = new ConnectedCapture();
        client.When(c => c.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        return capture;
    }

    private static DisconnectedCapture CaptureDisconnected(IMqttClient client)
    {
        var capture = new DisconnectedCapture();
        client.When(c => c.DisconnectedAsync += Arg.Any<Func<MqttClientDisconnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientDisconnectedEventArgs, Task>>());
        return capture;
    }

    private static MessageCapture CaptureMessage(IMqttClient client)
    {
        var capture = new MessageCapture();
        client.When(c => c.ApplicationMessageReceivedAsync +=
                        Arg.Any<Func<MqttApplicationMessageReceivedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttApplicationMessageReceivedEventArgs, Task>>());
        return capture;
    }

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() => new(
        false, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private sealed class ConnectedCapture
    {
        public Func<MqttClientConnectedEventArgs, Task>? Handler { get; set; }
    }

    private sealed class DisconnectedCapture
    {
        public Func<MqttClientDisconnectedEventArgs, Task>? Handler { get; set; }
    }

    private sealed class MessageCapture
    {
        public Func<MqttApplicationMessageReceivedEventArgs, Task>? Handler { get; set; }
    }

    private sealed class QueueClientFactory : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => false;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
