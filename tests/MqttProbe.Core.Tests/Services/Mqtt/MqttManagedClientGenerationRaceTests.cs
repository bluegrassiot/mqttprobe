using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientGenerationRaceTests
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

    private static MqttTopicFilter Filter(string topic) =>
        new MqttTopicFilterBuilder().WithTopic(topic).Build();

    [Test]
    public async Task Initial_resubscribe_failure_subscriber_awaiting_stop_does_not_deadlock()
    {
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<MqttClientSubscribeResult>(new InvalidOperationException("subscribe failed")));
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                client.IsConnected.Returns(true);
                await connected.Handler!(ConnectedArgs());
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        var sut = new MqttManagedClient(factory, null);

        var stopCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.SynchronizingSubscriptionsFailedAsync += async _ =>
        {
            await sut.StopAsync();
            stopCompleted.TrySetResult();
        };

        using var cts = new CancellationTokenSource();
        try
        {
            await sut.SubscribeAsync([Filter("a/b")]);
            _ = sut.StartAttempt(BuildOptions(), cts.Token);

            await stopCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cts.Cancel();
            sut.Dispose();
        }
    }

    [Test]
    public async Task Reconnect_resubscribe_failure_subscriber_awaiting_stop_does_not_deadlock()
    {
        var connectCalls = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<MqttClientSubscribeResult>(new InvalidOperationException("subscribe failed")));
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref connectCalls) == 1
                ? Task.FromException<MqttClientConnectResult>(new InvalidOperationException("initial"))
                : ReconnectAsync());

        async Task<MqttClientConnectResult> ReconnectAsync()
        {
            client.IsConnected.Returns(true);
            await connected.Handler!(ConnectedArgs());
            return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
        }

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        var sut = new MqttManagedClient(factory, null);

        var stopCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.SynchronizingSubscriptionsFailedAsync += async _ =>
        {
            await sut.StopAsync();
            stopCompleted.TrySetResult();
        };

        using var cts = new CancellationTokenSource();
        try
        {
            await sut.SubscribeAsync([Filter("a/b")]);
            var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(20)), cts.Token);
            (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);

            await stopCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cts.Cancel();
            sut.Dispose();
        }
    }

    [Test]
    public async Task Replaced_generation_does_not_republish_the_head_for_the_new_generation()
    {
        var publishStartedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<(string Client, string Topic)>();

        var clientA = Substitute.For<IMqttClient>();
        var connectedA = CaptureConnected(clientA);
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientA.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (published) published.Add(("A", ci.ArgAt<MqttApplicationMessage>(0).Topic));
                publishStartedA.TrySetResult();
                await releaseA.Task;
                return PublishResult();
            });

        var clientB = Substitute.For<IMqttClient>();
        var connectedB = CaptureConnected(clientB);
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientB.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lock (published) published.Add(("B", ci.ArgAt<MqttApplicationMessage>(0).Topic));
                return Task.FromResult(PublishResult());
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await sut.EnqueueAsync(Message("X"));
        clientA.IsConnected.Returns(true);

        var drainA = connectedA.Handler!(ConnectedArgs());
        await publishStartedA.Task;

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await sut.EnqueueAsync(Message("Y"));

        var drainB = connectedB.Handler!(ConnectedArgs());

        releaseA.TrySetResult();
        await drainA.WaitAsync(TimeSpan.FromSeconds(5));
        await drainB.WaitAsync(TimeSpan.FromSeconds(5));

        lock (published)
        {
            published.Count(p => p.Topic == "X").Should().Be(1, "the queue head is published once across generations");
            published.Should().ContainSingle(p => p.Topic == "Y").Which.Client.Should().Be("B");
        }
    }

    [Test]
    public async Task Retired_head_publish_is_accounted_before_the_new_generation_drains()
    {
        var publishStartedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<(string Client, string Topic)>();

        var clientA = Substitute.For<IMqttClient>();
        var connectedA = CaptureConnected(clientA);
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientA.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (published) published.Add(("A", ci.ArgAt<MqttApplicationMessage>(0).Topic));
                publishStartedA.TrySetResult();
                await releaseA.Task;
                return PublishResult();
            });

        var clientB = Substitute.For<IMqttClient>();
        var connectedB = CaptureConnected(clientB);
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientB.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lock (published) published.Add(("B", ci.ArgAt<MqttApplicationMessage>(0).Topic));
                return Task.FromResult(PublishResult());
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await sut.EnqueueAsync(Message("X"));
        clientA.IsConnected.Returns(true);

        var drainA = connectedA.Handler!(ConnectedArgs());
        await publishStartedA.Task;

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await sut.EnqueueAsync(Message("Y"));

        releaseA.TrySetResult();
        await drainA.WaitAsync(TimeSpan.FromSeconds(5));

        var drainB = connectedB.Handler!(ConnectedArgs());
        await drainB.WaitAsync(TimeSpan.FromSeconds(5));

        lock (published)
        {
            published.Count(p => p.Topic == "X").Should().Be(1, "a successful retired publish still accounts for the head");
            published.Should().ContainSingle(p => p.Topic == "Y").Which.Client.Should().Be("B");
        }
    }

    [Test]
    public async Task Stop_clear_then_same_instance_requeue_is_not_consumed_by_the_retired_drain()
    {
        var publishStartedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<(string Client, string Topic)>();

        var clientA = Substitute.For<IMqttClient>();
        var connectedA = CaptureConnected(clientA);
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientA.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (published) published.Add(("A", ci.ArgAt<MqttApplicationMessage>(0).Topic));
                publishStartedA.TrySetResult();
                await releaseA.Task;
                return PublishResult();
            });

        var clientB = Substitute.For<IMqttClient>();
        var connectedB = CaptureConnected(clientB);
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientB.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lock (published) published.Add(("B", ci.ArgAt<MqttApplicationMessage>(0).Topic));
                return Task.FromResult(PublishResult());
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        var messageX = Message("X");
        await sut.EnqueueAsync(messageX);
        clientA.IsConnected.Returns(true);

        var drainA = connectedA.Handler!(ConnectedArgs());
        await publishStartedA.Task;

        await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await sut.EnqueueAsync(messageX);

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        var drainB = connectedB.Handler!(ConnectedArgs());

        releaseA.TrySetResult();
        await drainA.WaitAsync(TimeSpan.FromSeconds(5));
        await drainB.WaitAsync(TimeSpan.FromSeconds(5));

        lock (published)
        {
            published.Count(p => p.Topic == "X").Should().Be(2, "the pre-clear delivery and the requeued instance both publish");
            published.Should().Contain(("B", "X"), "the stale retired drain must not consume the requeued head");
        }
    }

    [Test]
    public async Task ConnectedPublishWithOldEpoch_DoesNotPublishThroughReplacementGeneration()
    {
        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientA.IsConnected.Returns(true);

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientB.IsConnected.Returns(true);

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);
        var first = sut.StartAttempt(BuildOptions());
        (await first.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        var firstEpoch = sut.ConnectionEpoch;
        firstEpoch.Should().NotBeNull();

        var second = sut.StartAttempt(BuildOptions());
        (await second.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        (await sut.TryPublishConnectedAsync(Message("stale"), firstEpoch)).Should().BeFalse();
        await clientA.DidNotReceive().PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
        await clientB.DidNotReceive().PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConnectedPublish_PropagatesCallerCancellationToRawPublish()
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        client.IsConnected.Returns(true);
        client.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                publishStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(1));
                return PublishResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);
        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        using var cancellation = new CancellationTokenSource();

        var publish = sut.TryPublishConnectedAsync(Message("cancel"), sut.ConnectionEpoch, cancellation.Token);
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await FluentActions.Invoking(async () => await publish).Should().ThrowAsync<OperationCanceledException>();
    }

    private static ConnectedCapture CaptureConnected(IMqttClient client)
    {
        var capture = new ConnectedCapture();
        client.When(c => c.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        return capture;
    }

    private sealed class ConnectedCapture
    {
        public Func<MqttClientConnectedEventArgs, Task>? Handler { get; set; }
    }

    private sealed class QueueClientFactory : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => false;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
