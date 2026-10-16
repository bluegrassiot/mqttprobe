using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientCancellationTests
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

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private static int ConnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));

    private static int DisconnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.DisconnectAsync));

    [Test]
    public async Task Caller_cancel_of_pending_start_is_silent_and_does_not_reconnect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                return new MqttClientConnectResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };
        var outcomes = new List<MqttConnectOutcome>();
        sut.ConnectAttemptCompletedAsync += o => { lock (outcomes) outcomes.Add(o); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var start = sut.StartAsync(BuildOptions(), cts.Token);
        await started.Task;
        cts.Cancel();

        var act = async () => await start;
        await act.Should().ThrowAsync<OperationCanceledException>()
            .Where(e => e.CancellationToken == cts.Token);

        Volatile.Read(ref failures).Should().Be(0, "a caller cancel is not a connection failure");
        ConnectCount(client).Should().Be(1, "a canceled attempt must not schedule a reconnect");
        outcomes.Should().ContainSingle().Which.Status.Should().Be(MqttConnectStatus.Canceled);
        sut.IsStarted.Should().BeFalse();
    }

    [Test]
    public async Task Caller_cancel_wrapped_under_another_exception_is_still_silent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                }
                catch (OperationCanceledException oce)
                {
                    // MQTTnet can surface cancellation wrapped, e.g. MqttConnectingFailedException.
                    throw new InvalidOperationException("wrapped cancellation", oce);
                }

                return new MqttClientConnectResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var start = sut.StartAsync(BuildOptions(), cts.Token);
        await started.Task;
        cts.Cancel();

        var act = async () => await start;
        await act.Should().ThrowAsync<OperationCanceledException>();

        Volatile.Read(ref failures).Should().Be(0, "classification is by the generation, not the exception type");
        ConnectCount(client).Should().Be(1);
    }

    [Test]
    public async Task Late_success_from_a_canceled_generation_is_discarded()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await finish.Task;
                return new MqttClientConnectResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var connected = 0;
        var stateChanged = 0;
        sut.ConnectedAsync += _ => { Interlocked.Increment(ref connected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanged); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        await started.Task;
        cts.Cancel();

        client.IsConnected.Returns(true);
        client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
        await WaitUntilAsync(() => DisconnectCount(client) >= 1);

        finish.TrySetResult();
        var outcome = await attempt.Completion;

        outcome.Status.Should().Be(MqttConnectStatus.Canceled);
        Volatile.Read(ref connected).Should().Be(0, "a canceled generation must not surface success");
        Volatile.Read(ref stateChanged).Should().Be(0);
        DisconnectCount(client).Should().BeGreaterThanOrEqualTo(1,
            "the late-connected raw client is disconnected, not adopted");
        ConnectCount(client).Should().Be(1);
    }

    [Test]
    public async Task Delayed_disconnect_from_a_superseded_attempt_is_ignored()
    {
        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<MqttClientConnectResult>(new InvalidOperationException("refused")));

        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                bStarted.TrySetResult();
                await bFinish.Task;
                return new MqttClientConnectResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        var stateChanged = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanged); return Task.CompletedTask; };

        await sut.StartAsync(BuildOptions(TimeSpan.FromMinutes(5)));
        ConnectCount(clientA).Should().Be(1);

        var attemptB = sut.StartAttempt(BuildOptions());
        await bStarted.Task;

        clientA.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());
        await Task.Delay(50);

        clientB.IsConnected.Returns(true);
        clientB.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
        bFinish.TrySetResult();
        (await attemptB.Completion).Status.Should().Be(MqttConnectStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref stateChanged) >= 1);

        clientA.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());
        await Task.Delay(50);

        Volatile.Read(ref disconnected).Should().Be(0, "a superseded generation's disconnect is silent");
        Volatile.Read(ref stateChanged).Should().Be(1, "only B's connect changed connection state");
        ConnectCount(clientA).Should().Be(1, "A's reconnect loop was retired with it");
        ConnectCount(clientB).Should().Be(1);
    }

    [Test]
    public async Task Canceled_attempt_then_retry_connects()
    {
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                aStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                return new MqttClientConnectResult();
            });

        var clientB = Substitute.For<IMqttClient>();
        clientB.IsConnected.Returns(true);

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var attemptA = sut.StartAttempt(BuildOptions(), cts.Token);
        await aStarted.Task;
        cts.Cancel();
        (await attemptA.Completion).Status.Should().Be(MqttConnectStatus.Canceled);

        var attemptB = sut.StartAttempt(BuildOptions());
        (await attemptB.Completion).Status.Should().Be(MqttConnectStatus.Connected);

        sut.IsConnected.Should().BeTrue();
        Volatile.Read(ref failures).Should().Be(0);
        ConnectCount(clientB).Should().Be(1);
    }

    [Test]
    public async Task Stop_cancels_an_in_flight_connect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                return new MqttClientConnectResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task;

        await sut.StopAsync();

        (await attempt.Completion).Status.Should().Be(MqttConnectStatus.Canceled);
        sut.IsStarted.Should().BeFalse();
        ConnectCount(client).Should().Be(1);
    }

    [Test]
    public async Task Dispose_cancels_an_in_flight_connect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                return new MqttClientConnectResult();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        var sut = new MqttManagedClient(factory, null);

        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task;

        sut.Dispose();

        (await attempt.Completion).Status.Should().Be(MqttConnectStatus.Canceled);
    }

    [Test]
    public async Task Real_failure_raises_once_and_reconnects_unchanged()
    {
        var boom = new InvalidOperationException("refused");
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<MqttClientConnectResult>(boom),
                _ => Task.FromResult(new MqttClientConnectResult()));

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var captured = new List<MqttConnectingFailedEventArgs>();
        sut.ConnectingFailedAsync += e => { lock (captured) captured.Add(e); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(20)));
        (await attempt.Completion).Status.Should().Be(MqttConnectStatus.Failed);

        await WaitUntilAsync(() => ConnectCount(client) >= 2);
        ConnectCount(client).Should().BeGreaterThanOrEqualTo(2, "a real failure still reconnects");
        captured.Should().ContainSingle();
        captured[0].Exception.Should().BeSameAs(boom);
    }

    [Test]
    public async Task Attempt_id_is_correlated_on_success()
    {
        var client = Substitute.For<IMqttClient>();
        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var outcomes = new List<MqttConnectOutcome>();
        sut.ConnectAttemptCompletedAsync += o => { lock (outcomes) outcomes.Add(o); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions());
        var outcome = await attempt.Completion;

        attempt.Id.IsNone.Should().BeFalse();
        outcome.AttemptId.Should().Be(attempt.Id);
        outcome.Status.Should().Be(MqttConnectStatus.Connected);
        outcomes.Should().ContainSingle().Which.AttemptId.Should().Be(attempt.Id);
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

    private sealed class QueueClientFactory : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => false;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
