using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientScopedStopTests
{
    private static MqttManagedClientOptions BuildOptions() => new()
    {
        ClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("test")
            .Build(),
        AutoReconnectDelay = TimeSpan.FromMinutes(5)
    };

    private static MqttClientConnectResult Success() =>
        new() { ResultCode = MqttClientConnectResultCode.Success };

    private static int ConnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));

    private static int DisconnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.DisconnectAsync));

    private static int PublishCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.PublishAsync));

    [Test]
    public async Task StopAttemptAsync_stops_the_targeted_attempt()
    {
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));
        client.IsConnected.Returns(true);

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await sut.StopAttemptAsync(attempt.Id).WaitAsync(TimeSpan.FromSeconds(5));

        sut.IsStarted.Should().BeFalse();
        sut.IsConnected.Should().BeFalse();
        DisconnectCount(client).Should().Be(1);
        ConnectCount(client).Should().Be(1);
    }

    [Test]
    public async Task StopAttemptAsync_retires_only_the_targeted_attempt_while_a_replacement_is_current()
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));
        clientA.IsConnected.Returns(true);
        clientA.DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
            });

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));
        clientB.IsConnected.Returns(true);

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = sut.StopAttemptAsync(attemptA.Id);
        await Task.Delay(100);

        stop.IsCompleted.Should().BeFalse("the scoped stop waits for the targeted attempt's cleanup");
        sut.IsConnected.Should().BeTrue("the replacement generation stays current and connected");
        DisconnectCount(clientB).Should().Be(0, "a scoped stop must never stop the replacement connection");
        ConnectCount(clientB).Should().Be(1);
        DisconnectCount(clientA).Should().Be(1);

        releaseCleanup.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        sut.IsConnected.Should().BeTrue();
        DisconnectCount(clientB).Should().Be(0);
        ConnectCount(clientB).Should().Be(1);
    }

    [Test]
    public async Task StopAttemptAsync_with_an_unknown_id_leaves_the_session_and_pending_publishes_alone()
    {
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await sut.EnqueueAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scoped/pending")
            .WithPayload("queued")
            .Build());

        await sut.StopAttemptAsync(new MqttConnectAttemptId(attempt.Id.Value + 100)).WaitAsync(TimeSpan.FromSeconds(5));

        sut.IsStarted.Should().BeTrue("an unknown attempt id must not stop the tracked session");
        DisconnectCount(client).Should().Be(0);

        _ = connected.Handler!(new MqttClientConnectedEventArgs(new MqttClientConnectResult()));
        await WaitUntilAsync(() => PublishCount(client) == 1);
        PublishCount(client).Should().Be(1, "a scoped stop must not clear the pending publish queue");
    }

    [Test]
    public async Task StopAttemptAsync_of_the_current_established_attempt_reports_one_deliberate_disconnect()
    {
        var isConnected = false;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.IsConnected.Returns(_ => isConnected);
        client.DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                isConnected = false;
                return Task.CompletedTask;
            });
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                isConnected = true;
                await connected.Handler!(new MqttClientConnectedEventArgs(new MqttClientConnectResult()));
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        var stateChanged = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanged); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref stateChanged) == 1);
        Volatile.Write(ref disconnected, 0);
        Volatile.Write(ref stateChanged, 0);

        await sut.StopAttemptAsync(attempt.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Volatile.Read(ref disconnected) == 1 && Volatile.Read(ref stateChanged) == 1);

        Volatile.Read(ref disconnected).Should().Be(1,
            "stopping the current established attempt reports one deliberate disconnect");
        Volatile.Read(ref stateChanged).Should().Be(1);

        await sut.StopAttemptAsync(attempt.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref disconnected).Should().Be(1, "the deliberate stop is reported once per attempt");
        Volatile.Read(ref stateChanged).Should().Be(1);
    }

    [Test]
    public async Task StopAttemptAsync_of_a_superseded_established_attempt_stays_silent()
    {
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientA = Substitute.For<IMqttClient>();
        var connectedA = CaptureConnected(clientA);
        clientA.IsConnected.Returns(true);
        clientA.DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
            });
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await connectedA.Handler!(new MqttClientConnectedEventArgs(new MqttClientConnectResult()));
                return Success();
            });

        var clientB = Substitute.For<IMqttClient>();
        clientB.IsConnected.Returns(true);
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        var stateChanged = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanged); return Task.CompletedTask; };

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref stateChanged) == 1);
        Volatile.Write(ref disconnected, 0);
        Volatile.Write(ref stateChanged, 0);

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = sut.StopAttemptAsync(attemptA.Id);
        await Task.Delay(100);
        releaseCleanup.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Volatile.Read(ref disconnected).Should().Be(0, "a superseded attempt reports no deliberate disconnect");
        Volatile.Read(ref stateChanged).Should().Be(0);
        sut.IsConnected.Should().BeTrue();
    }

    [Test]
    public async Task StopAttemptAsync_of_a_pending_attempt_stays_silent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        var stateChanged = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanged); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await sut.StopAttemptAsync(attempt.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        Volatile.Read(ref disconnected).Should().Be(0, "an attempt that never reported success has no session to close");
        Volatile.Read(ref stateChanged).Should().Be(0);
        sut.IsStarted.Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
    }

    private static HandlerCapture<MqttClientConnectedEventArgs> CaptureConnected(IMqttClient client)
    {
        var capture = new HandlerCapture<MqttClientConnectedEventArgs>();
        client.When(c => c.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        return capture;
    }

    private sealed class HandlerCapture<T>
    {
        public Func<T, Task>? Handler { get; set; }
    }

    private sealed class QueueClientFactory : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => false;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
