using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientCancellationNotificationTests
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

    private static MqttClientConnectedEventArgs ConnectedArgs() => new(new MqttClientConnectResult());

    [Test]
    public async Task Caller_cancel_after_connect_reports_raw_disconnect_then_managed_disconnect_and_state_once()
    {
        var connectedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new List<string>();

        var isConnected = false;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.IsConnected.Returns(_ => isConnected);
        client.DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                lock (journal) journal.Add("raw-disconnect");
                isConnected = false;
                return Task.CompletedTask;
            });
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                isConnected = true;
                await connected.Handler!(ConnectedArgs());
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var outcomes = new List<MqttConnectOutcome>();
        sut.ConnectionStateChangedAsync += async _ =>
        {
            connectedObserved.TrySetResult();
            await release.Task;
        };
        sut.ConnectionStateChangedAsync += _ =>
        {
            lock (journal) journal.Add("managed-state");
            return Task.CompletedTask;
        };
        sut.DisconnectedAsync += _ =>
        {
            lock (journal) journal.Add("managed-disconnect");
            return Task.CompletedTask;
        };
        sut.ConnectAttemptCompletedAsync += o => { lock (outcomes) outcomes.Add(o); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var start = sut.StartAsync(BuildOptions(), cts.Token);
        await connectedObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        try
        {
            await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Snapshot(journal).Should().Equal("raw-disconnect", "managed-disconnect");
            isConnected.Should().BeFalse("the raw client disconnected before the managed notifications");
        }
        finally
        {
            release.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await WaitUntilAsync(() => Snapshot(journal).Count == 3);
        Snapshot(journal).Should().Equal("raw-disconnect", "managed-disconnect", "managed-state");
        outcomes.Should().ContainSingle().Which.Status.Should().Be(MqttConnectStatus.Connected,
            "a caller cancel must not overturn an already settled success");
    }

    [Test]
    public async Task Caller_cancel_of_a_never_connected_attempt_is_silent()
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

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);

        Volatile.Read(ref disconnected).Should().Be(0, "cancelling before the client ever connected is silent");
        Volatile.Read(ref stateChanged).Should().Be(0);
    }

    [Test]
    public async Task Replacing_attempt_suppresses_the_superseded_caller_cancel_notification()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
            .Returns(async ci =>
            {
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        var stateChanged = 0;
        sut.ConnectedAsync += async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanged); return Task.CompletedTask; };

        using var aCts = new CancellationTokenSource();
        var attemptA = sut.StartAttempt(BuildOptions(), aCts.Token);
        (await attemptA.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _ = sut.StartAttempt(BuildOptions());

        try
        {
            aCts.Cancel();

            Volatile.Read(ref disconnected).Should().Be(0,
                "a superseded generation must not report a caller-cancel disconnect");
            Volatile.Read(ref stateChanged).Should().Be(0,
                "a superseded generation must not report a caller-cancel state change");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task Caller_cancel_disconnect_subscriber_awaiting_stop_does_not_deadlock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await connected.Handler!(ConnectedArgs());
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var stopCompleted = 0;
        sut.ConnectedAsync += async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        sut.DisconnectedAsync += async _ =>
        {
            await sut.StopAsync();
            Interlocked.Increment(ref stopCompleted);
        };

        using var cts = new CancellationTokenSource();
        var start = sut.StartAsync(BuildOptions(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref stopCompleted) == 1);
            Volatile.Read(ref stopCompleted).Should().Be(1,
                "the caller-cancel disconnect subscriber's Stop must complete");
        }
        finally
        {
            release.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static List<string> Snapshot(List<string> journal)
    {
        lock (journal)
            return [.. journal];
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
