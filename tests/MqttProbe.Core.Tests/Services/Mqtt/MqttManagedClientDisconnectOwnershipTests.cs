using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientDisconnectOwnershipTests
{
    private static MqttManagedClientOptions BuildOptions(TimeSpan? reconnect = null) => new()
    {
        ClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("test")
            .Build(),
        AutoReconnectDelay = reconnect ?? TimeSpan.FromMinutes(5)
    };

    private static MqttClientConnectResult Success() =>
        new() { ResultCode = MqttClientConnectResultCode.Success };

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private static MqttClientConnectedEventArgs ConnectedArgs() => new(new MqttClientConnectResult());

    private static int ConnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));

    [Test]
    public async Task Replaced_generation_skips_remaining_disconnect_subscribers_and_trailing_state()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));
        var disconnectedA = CaptureDisconnected(clientA);

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var secondRan = 0;
        var stateCount = 0;
        sut.DisconnectedAsync += async _ =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref secondRan); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateCount); return Task.CompletedTask; };

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        var dispatchA = disconnectedA.Handler!(DisconnectedArgs());
        await firstEntered.Task;

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        releaseFirst.TrySetResult();
        await dispatchA.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref secondRan).Should().Be(0, "a replaced generation must not reach its later disconnect subscribers");
        Volatile.Read(ref stateCount).Should().Be(0, "a replaced generation must not emit trailing connection state");
    }

    [Test]
    public async Task Revoked_gate_without_replacement_still_delivers_disconnect_and_state()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));
        var disconnected = CaptureDisconnected(client);

        var gate = new RevocableSessionActivityGate();
        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null, gate);

        var secondRan = 0;
        var stateCount = 0;
        sut.DisconnectedAsync += async _ =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref secondRan); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateCount); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        gate.Revoke();

        var dispatch = disconnected.Handler!(DisconnectedArgs());
        await firstEntered.Task;
        releaseFirst.TrySetResult();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref secondRan).Should().Be(1, "a revoked but still-owned session still delivers disconnect");
        Volatile.Read(ref stateCount).Should().Be(1, "trailing connection state is delivered even after revocation");
        ConnectCount(client).Should().Be(1, "a revoked gate must not start a reconnect");
    }

    [Test]
    public async Task State_subscriber_replacing_the_generation_skips_the_second_state_subscriber()
    {
        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));
        var disconnectedA = CaptureDisconnected(clientA);

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var firstState = 0;
        var secondState = 0;
        sut.ConnectionStateChangedAsync += _ =>
        {
            Interlocked.Increment(ref firstState);
            sut.StartAttempt(BuildOptions());
            return Task.CompletedTask;
        };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref secondState); return Task.CompletedTask; };

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await disconnectedA.Handler!(DisconnectedArgs()).WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref firstState).Should().Be(1);
        Volatile.Read(ref secondState).Should().Be(0, "a state subscriber that replaced the generation must halt the rest");
    }

    [Test]
    public async Task Stop_skips_a_superseded_generation_still_awaiting_its_cleanup()
    {
        var cleanupAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanupA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));
        clientA.IsConnected.Returns(true);
        clientA.DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                cleanupAEntered.TrySetResult();
                await releaseCleanupA.Task;
            });

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        var stateCount = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateCount); return Task.CompletedTask; };

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await cleanupAEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = sut.StopAsync();
        releaseCleanupA.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref disconnected).Should().Be(1, "only the explicitly stopped generation reports a deliberate disconnect");
        Volatile.Read(ref stateCount).Should().Be(1, "a superseded generation must not emit deliberate-stop state");
    }

    [Test]
    public async Task New_attempt_started_mid_deliberate_stop_skips_the_remaining_dispatch()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var secondDisconnected = 0;
        var stateCount = 0;
        sut.DisconnectedAsync += async _ =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref secondDisconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateCount); return Task.CompletedTask; };

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        var stop = sut.StopAsync();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        releaseFirst.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref secondDisconnected).Should().Be(0, "a superseded generation must not reach its later deliberate-stop subscribers");
        Volatile.Read(ref stateCount).Should().Be(0, "a superseded generation must not emit trailing deliberate-stop state");
    }

    [Test]
    public async Task Connected_state_subscriber_replacing_the_generation_skips_the_second_state_subscriber()
    {
        var clientA = Substitute.For<IMqttClient>();
        var connectedA = CaptureConnected(clientA);
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await connectedA.Handler!(ConnectedArgs());
                return Success();
            });

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Success()));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var firstState = 0;
        var secondState = 0;
        sut.ConnectionStateChangedAsync += _ =>
        {
            Interlocked.Increment(ref firstState);
            sut.StartAttempt(BuildOptions());
            return Task.CompletedTask;
        };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref secondState); return Task.CompletedTask; };

        var attemptA = sut.StartAttempt(BuildOptions());
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        Volatile.Read(ref firstState).Should().Be(1);
        Volatile.Read(ref secondState).Should().Be(0, "a connected-state subscriber that replaced the generation must halt the rest");
    }

    private static HandlerCapture<MqttClientDisconnectedEventArgs> CaptureDisconnected(IMqttClient client)
    {
        var capture = new HandlerCapture<MqttClientDisconnectedEventArgs>();
        client.When(c => c.DisconnectedAsync += Arg.Any<Func<MqttClientDisconnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientDisconnectedEventArgs, Task>>());
        return capture;
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
