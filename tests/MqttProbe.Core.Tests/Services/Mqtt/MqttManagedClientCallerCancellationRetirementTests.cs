using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientCallerCancellationRetirementTests
{
    [Test]
    public async Task Caller_cancel_behind_a_blocked_outcome_subscriber_still_retires_the_session()
    {
        var rawConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOutcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                await connected.Handler!(ConnectedArgs());
                rawConnected.TrySetResult();
                await releaseConnect.Task;
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var managedDisconnects = 0;
        var stateChanges = 0;
        MqttConnectOutcome? captured = null;
        sut.ConnectAttemptCompletedAsync += async outcome =>
        {
            captured = outcome;
            outcomeEntered.TrySetResult();
            await releaseOutcome.Task;
        };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref managedDisconnects); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanges); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var start = sut.StartAsync(BuildOptions(), cts.Token);
        await rawConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseConnect.TrySetResult();
        await outcomeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancelBarrier = new CallerCancelBarrier(cts);
        var cancel = Task.Run(cts.Cancel);
        try
        {
            await cancelBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            releaseOutcome.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5));

            await WaitUntilAsync(() => Volatile.Read(ref managedDisconnects) == 1 && Volatile.Read(ref stateChanges) == 1);
            Volatile.Read(ref managedDisconnects).Should().Be(1,
                "a cancellation the disposal could drop is still settled as a deliberate disconnect");
            Volatile.Read(ref stateChanges).Should().Be(1);
            ConnectCount(client).Should().Be(1);
            DisconnectCount(client).Should().Be(1, "the retired generation tears its raw client down");
            sut.IsStarted.Should().BeFalse("the cancelled attempt retires once its registration is gone");
            captured!.Status.Should().Be(MqttConnectStatus.Connected,
                "the settled success is not overturned by the later cancellation");
        }
        finally
        {
            cancelBarrier.Release();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Start_waits_for_an_executing_cancel_callback_before_completing()
    {
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOutcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDisconnect = new ManualResetEventSlim(false);

        var isConnected = false;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.IsConnected.Returns(_ => isConnected);
        client.DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Blocks the calling (callback) stack synchronously, which is what holding an
                // executing cancellation callback looks like from the operation's side.
                disconnectEntered.TrySetResult();
                releaseDisconnect.Wait();
                isConnected = false;
                return Task.CompletedTask;
            });
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                isConnected = true;
                await connected.Handler!(ConnectedArgs());
                await releaseConnect.Task;
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var managedDisconnects = 0;
        MqttConnectOutcome? captured = null;
        sut.ConnectAttemptCompletedAsync += async outcome =>
        {
            captured = outcome;
            outcomeEntered.TrySetResult();
            await releaseOutcome.Task;
        };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref managedDisconnects); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var start = sut.StartAsync(BuildOptions(), cts.Token);
        releaseConnect.TrySetResult();
        await outcomeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancel = Task.Run(cts.Cancel);
        await disconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            releaseOutcome.TrySetResult();
            await Task.Delay(200);
            start.IsCompleted.Should().BeFalse(
                "registration disposal waits for the cancellation callback that is still running");
        }
        finally
        {
            releaseDisconnect.Set();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await start.WaitAsync(TimeSpan.FromSeconds(5));
        captured!.Status.Should().Be(MqttConnectStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref managedDisconnects) == 1);
        Volatile.Read(ref managedDisconnects).Should().Be(1,
            "the cancellation is reported once, without waiting on a blocked subscriber");
    }

    [Test]
    public async Task Immediate_cancel_after_start_returns_leaves_the_session_owned()
    {
        var rawConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var isConnected = false;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
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
                await connected.Handler!(ConnectedArgs());
                rawConnected.TrySetResult();
                await releaseConnect.Task;
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var managedDisconnects = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref managedDisconnects); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var start = sut.StartAsync(BuildOptions(), cts.Token);
        await rawConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseConnect.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        await Task.Delay(50);

        sut.IsStarted.Should().BeTrue("a cancel after Start returns must not retire the established session");
        ConnectCount(client).Should().Be(1);
        DisconnectCount(client).Should().Be(0, "the session is left connected");
        Volatile.Read(ref managedDisconnects).Should().Be(0);

        _ = disconnected.Handler!(DisconnectedArgs());
        await WaitUntilAsync(() => Volatile.Read(ref managedDisconnects) == 1);
        Volatile.Read(ref managedDisconnects).Should().Be(1,
            "the session is still owned and dispatches its raw disconnect");
    }

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

    private static MqttClientConnectedEventArgs ConnectedArgs() => new(new MqttClientConnectResult());

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private static int ConnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));

    private static int DisconnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.DisconnectAsync));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(20);
            waited += 20;
        }
    }

    // Registered after the library's own registration, so LIFO ordering runs this callback first
    // and holds the retire callback out of the caller-token window the tests exercise.
    private sealed class CallerCancelBarrier : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly CancellationTokenRegistration _registration;

        public CallerCancelBarrier(CancellationTokenSource cts)
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _registration = cts.Token.Register(() =>
            {
                Entered.TrySetResult();
                _release.Wait();
            });
        }

        public TaskCompletionSource Entered { get; }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _registration.Dispose();
            _release.Dispose();
        }
    }

    private static HandlerCapture<MqttClientConnectedEventArgs> CaptureConnected(IMqttClient client)
    {
        var capture = new HandlerCapture<MqttClientConnectedEventArgs>();
        client.When(c => c.ConnectedAsync += Arg.Any<Func<MqttClientConnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientConnectedEventArgs, Task>>());
        return capture;
    }

    private static HandlerCapture<MqttClientDisconnectedEventArgs> CaptureDisconnected(IMqttClient client)
    {
        var capture = new HandlerCapture<MqttClientDisconnectedEventArgs>();
        client.When(c => c.DisconnectedAsync += Arg.Any<Func<MqttClientDisconnectedEventArgs, Task>>())
            .Do(ci => capture.Handler = ci.Arg<Func<MqttClientDisconnectedEventArgs, Task>>());
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
