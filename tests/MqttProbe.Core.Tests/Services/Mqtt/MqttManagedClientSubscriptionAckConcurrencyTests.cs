using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientSubscriptionAckConcurrencyTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    private const string Restored = "restored";
    private const string Live = "live";

    private static MqttManagedClientOptions BuildOptions(TimeSpan? reconnect = null) => new()
    {
        ClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("test")
            .Build(),
        AutoReconnectDelay = reconnect ?? TimeSpan.FromMinutes(5)
    };

    private static MqttClientConnectedEventArgs ConnectedArgs() => new(new MqttClientConnectResult());

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() => new(
        false, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private static MqttTopicFilter Filter(string topic) => new MqttTopicFilterBuilder().WithTopic(topic).Build();

    private static MqttClientSubscribeResult SubscribeResult(MqttClientSubscribeOptions options) => new(
        1,
        options.TopicFilters.Select(filter => new MqttClientSubscribeResultItem(
            filter, MqttClientSubscribeResultCode.GrantedQoS0)).ToArray(),
        null!,
        []);

    private static MqttClientSubscribeResult SubscribeResult(string topic) => new(
        1, [new MqttClientSubscribeResultItem(Filter(topic), MqttClientSubscribeResultCode.GrantedQoS0)], null!, []);

    [TestCase("A")]
    [TestCase("B")]
    public async Task DisjointSubscribeAcknowledgedOutOfOrderKeepsBothFilters(string held)
    {
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeld = new TaskCompletionSource<MqttClientSubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = held == "A" ? "B" : "A";
        MqttClientSubscribeOptions heldOptions = null!;
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
                var options = call.Arg<MqttClientSubscribeOptions>();
                if (options.TopicFilters.Any(filter => filter.Topic == held))
                {
                    heldOptions = options;
                    heldStarted.TrySetResult();
                    return releaseHeld.Task;
                }
                return Task.FromResult(SubscribeResult(options));
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        try
        {
            var attempt = sut.StartAttempt(BuildOptions());
            (await attempt.Completion.WaitAsync(_timeout)).Status.Should().Be(MqttConnectStatus.Connected);

            var heldSubscription = sut.SubscribeAsync([Filter(held)]);
            await heldStarted.Task.WaitAsync(_timeout);
            await sut.SubscribeAsync([Filter(peer)]).WaitAsync(_timeout);

            releaseHeld.SetResult(SubscribeResult(heldOptions));
            await heldSubscription.WaitAsync(_timeout);

            sut.LiveSubscriptionSnapshot!.ConfirmedFilters.Should().BeEquivalentTo([held, peer]);
        }
        finally
        {
            releaseHeld.TrySetResult(SubscribeResult(held));
        }
    }

    [Test]
    public async Task PartialAcknowledgementStillThrowsAndKeepsConcurrentDisjointGrant()
    {
        var partialStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePartial = new TaskCompletionSource<MqttClientSubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                var options = call.Arg<MqttClientSubscribeOptions>();
                if (options.TopicFilters.Any(filter => filter.Topic == "partial-denied"))
                {
                    partialStarted.TrySetResult();
                    return releasePartial.Task;
                }
                return Task.FromResult(SubscribeResult(options));
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        try
        {
            var attempt = sut.StartAttempt(BuildOptions());
            (await attempt.Completion.WaitAsync(_timeout)).Status.Should().Be(MqttConnectStatus.Connected);

            var partial = sut.SubscribeAsync([Filter("partial-granted"), Filter("partial-denied")]);
            await partialStarted.Task.WaitAsync(_timeout);
            await sut.SubscribeAsync([Filter("peer")]).WaitAsync(_timeout);

            releasePartial.SetResult(new MqttClientSubscribeResult(
                1,
                [
                    new MqttClientSubscribeResultItem(Filter("partial-granted"), MqttClientSubscribeResultCode.GrantedQoS1),
                    new MqttClientSubscribeResultItem(Filter("partial-denied"), MqttClientSubscribeResultCode.NotAuthorized)
                ],
                null!,
                []));

            await FluentActions.Invoking(() => partial.WaitAsync(_timeout))
                .Should().ThrowAsync<InvalidOperationException>();

            sut.LiveSubscriptionSnapshot!.ConfirmedFilters.Should()
                .BeEquivalentTo(["partial-granted", "peer"]);
        }
        finally
        {
            releasePartial.TrySetResult(SubscribeResult("partial-granted"));
        }
    }

    [Test]
    public async Task RejectedUnsubscribeAfterConcurrentSubscribeIsReported()
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
            .Returns(_ =>
            {
                unsubscribeStarted.TrySetResult();
                return releaseUnsubscribe.Task;
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        try
        {
            var attempt = sut.StartAttempt(BuildOptions());
            (await attempt.Completion.WaitAsync(_timeout)).Status.Should().Be(MqttConnectStatus.Connected);
            await sut.SubscribeAsync([Filter("B")]);

            var unsubscribe = sut.UnsubscribeAsync(["B"]);
            await unsubscribeStarted.Task.WaitAsync(_timeout);
            await sut.SubscribeAsync([Filter("C")]).WaitAsync(_timeout);

            releaseUnsubscribe.SetResult(new MqttClientUnsubscribeResult(
                1, [new MqttClientUnsubscribeResultItem("B", MqttClientUnsubscribeResultCode.NotAuthorized)], null!, []));

            await FluentActions.Invoking(() => unsubscribe.WaitAsync(_timeout))
                .Should().ThrowAsync<InvalidOperationException>();

            sut.LiveSubscriptionSnapshot.Should().BeNull();
            await sut.SubscribeAsync([Filter("D")]).WaitAsync(_timeout);
            sut.LiveSubscriptionSnapshot!.ConfirmedFilters.Should().BeEquivalentTo(["C", "D"]);
        }
        finally
        {
            releaseUnsubscribe.TrySetResult(new MqttClientUnsubscribeResult(
                1, [new MqttClientUnsubscribeResultItem("B", MqttClientUnsubscribeResultCode.Success)], null!, []));
        }
    }

    [Test]
    public async Task RestorationAcknowledgementSurvivesConcurrentLiveSubscribe()
    {
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource<MqttClientSubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        MqttClientSubscribeOptions restoreOptions = null!;
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
                var options = call.Arg<MqttClientSubscribeOptions>();
                if (options.TopicFilters.Any(filter => filter.Topic == Restored))
                {
                    restoreOptions = options;
                    restoreStarted.TrySetResult();
                    return releaseRestore.Task;
                }
                return Task.FromResult(SubscribeResult(options));
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        try
        {
            await sut.SubscribeAsync([Filter(Restored)]);

            var attempt = sut.StartAttempt(BuildOptions());
            await restoreStarted.Task.WaitAsync(_timeout);
            await sut.SubscribeAsync([Filter(Live)]).WaitAsync(_timeout);

            releaseRestore.SetResult(SubscribeResult(restoreOptions));
            (await attempt.Completion.WaitAsync(_timeout)).Status.Should().Be(MqttConnectStatus.Connected);

            sut.LiveSubscriptionSnapshot!.ConfirmedFilters.Should().BeEquivalentTo([Restored, Live]);
        }
        finally
        {
            releaseRestore.TrySetResult(SubscribeResult(Restored));
        }
    }

    [Test]
    public async Task AcknowledgementFromRetiredLifetimeDoesNotPopulateTheNewLifetime()
    {
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource<MqttClientSubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource<MqttClientSubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribeCount = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
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
                if (Interlocked.Increment(ref subscribeCount) == 1)
                {
                    staleStarted.TrySetResult();
                    return releaseStale.Task;
                }
                restoreStarted.TrySetResult();
                return releaseRestore.Task;
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        try
        {
            var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(10)));
            (await attempt.Completion.WaitAsync(_timeout)).Status.Should().Be(MqttConnectStatus.Connected);
            var firstEpoch = sut.ConnectionEpoch;

            var stale = sut.SubscribeAsync([Filter(Restored)]);
            await staleStarted.Task.WaitAsync(_timeout);

            client.IsConnected.Returns(false);
            await disconnected.Handler!(DisconnectedArgs());

            releaseStale.SetResult(SubscribeResult(Restored));
            await stale.WaitAsync(_timeout);
            await restoreStarted.Task.WaitAsync(_timeout);

            sut.ConnectionEpoch.Should().NotBe(firstEpoch);
            sut.LiveSubscriptionSnapshot.Should().BeNull();

            releaseRestore.SetResult(SubscribeResult(Restored));
            var snapshot = await WaitForConfirmedAsync(sut, Restored);
            snapshot.ConfirmedFilters.Should().ContainSingle().Which.Should().Be(Restored);
        }
        finally
        {
            releaseStale.TrySetResult(SubscribeResult(Restored));
            releaseRestore.TrySetResult(SubscribeResult(Restored));
        }
    }

    private static async Task<MqttSubscriptionSnapshot> WaitForConfirmedAsync(
        MqttManagedClient sut, string topic)
    {
        var deadline = DateTime.UtcNow.Add(_timeout);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = sut.LiveSubscriptionSnapshot;
            if (snapshot is not null && snapshot.ConfirmedFilters.Contains(topic))
                return snapshot;
            await Task.Delay(10);
        }
        throw new InvalidOperationException($"Filter {topic} was never confirmed.");
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

    private sealed class ConnectedCapture
    {
        public Func<MqttClientConnectedEventArgs, Task>? Handler { get; set; }
    }

    private sealed class DisconnectedCapture
    {
        public Func<MqttClientDisconnectedEventArgs, Task>? Handler { get; set; }
    }

    private sealed class QueueClientFactory : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => false;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
