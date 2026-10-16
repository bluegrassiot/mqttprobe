using MQTTnet;
using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientCallerCancellationIntentTests
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

    private static MqttClientConnectedEventArgs ConnectedArgs() => new(new MqttClientConnectResult());

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    private static int ConnectCount(IMqttClient client) =>
        client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMqttClient.ConnectAsync));

    [Test]
    public async Task Raw_disconnect_inside_the_caller_cancellation_window_stays_silent_and_schedules_no_reconnect()
    {
        var rawConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var isConnected = false;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.IsConnected.Returns(_ => isConnected);
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
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref managedDisconnects); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanges); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.Zero), cts.Token);
        await rawConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancelBarrier = new CallerCancelBarrier(cts);
        var cancel = Task.Run(cts.Cancel);
        try
        {
            await cancelBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            isConnected = false;
            _ = disconnected.Handler!(DisconnectedArgs());
            await WaitUntilAsync(() => ConnectCount(client) > 1, 300);

            Volatile.Read(ref managedDisconnects).Should().Be(0,
                "a raw disconnect dispatched after the caller token was requested must not notify");
            Volatile.Read(ref stateChanges).Should().Be(0,
                "a raw disconnect dispatched after the caller token was requested must not change state");
            ConnectCount(client).Should().Be(1,
                "a raw disconnect dispatched after the caller token was requested must not schedule a reconnect");
        }
        finally
        {
            cancelBarrier.Release();
            releaseConnect.TrySetResult();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        }

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        await Task.Delay(100);
        Volatile.Read(ref managedDisconnects).Should().Be(0,
            "a caller cancel that settles the attempt as canceled stays silent");
        Volatile.Read(ref stateChanges).Should().Be(0);
        ConnectCount(client).Should().Be(1);
    }

    [Test]
    public async Task Reconnect_scheduled_before_a_caller_cancel_does_not_connect_inside_the_cancellation_window()
    {
        var calls = 0;
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<MqttClientConnectResult>(new InvalidOperationException("refused"))
                : Task.FromResult(Success()));

        using var gate = new ReconnectCheckpointGate();
        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null, gate);

        var managedDisconnects = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref managedDisconnects); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var starting = Task.Run(() => sut.StartAttempt(BuildOptions(TimeSpan.Zero), cts.Token));
        await gate.CheckpointReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancelBarrier = new CallerCancelBarrier(cts);
        var cancel = Task.Run(cts.Cancel);
        try
        {
            await cancelBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            gate.Release();
            await WaitUntilAsync(() => ConnectCount(client) > 1, 300);

            ConnectCount(client).Should().Be(1,
                "a reconnect held at its first checkpoint must not connect once the caller token is requested");
        }
        finally
        {
            gate.Release();
            cancelBarrier.Release();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var attempt = await starting.WaitAsync(TimeSpan.FromSeconds(5));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed,
            "the failure was classified before the caller cancelled");
        Volatile.Read(ref managedDisconnects).Should().Be(0,
            "a never-connected attempt reports no disconnect when the caller cancels");
    }

    [Test]
    public async Task Caller_cancel_between_disconnect_and_state_subscribers_suppresses_the_trailing_dispatch()
    {
        var rawConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDisconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var disconnected = CaptureDisconnected(client);
        client.IsConnected.Returns(true);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await connected.Handler!(ConnectedArgs());
                rawConnected.TrySetResult();
                await releaseConnect.Task;
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnectSubscribers = 0;
        var stateChanges = 0;
        sut.DisconnectedAsync += async _ =>
        {
            Interlocked.Increment(ref disconnectSubscribers);
            firstDisconnectEntered.TrySetResult();
            await releaseFirstDisconnect.Task;
        };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnectSubscribers); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanges); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        await rawConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _ = disconnected.Handler!(DisconnectedArgs());
        await firstDisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancelBarrier = new CallerCancelBarrier(cts);
        var cancel = Task.Run(cts.Cancel);
        try
        {
            await cancelBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseFirstDisconnect.TrySetResult();
            await Task.Delay(300);

            Volatile.Read(ref disconnectSubscribers).Should().Be(1,
                "a caller cancel must halt the remaining disconnect subscribers");
            Volatile.Read(ref stateChanges).Should().Be(0,
                "a caller cancel must suppress the trailing connection state notification");
        }
        finally
        {
            cancelBarrier.Release();
            releaseConnect.TrySetResult();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        }

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        Volatile.Read(ref disconnectSubscribers).Should().Be(1, "the canceled attempt stays silent");
        Volatile.Read(ref stateChanges).Should().Be(0);
        ConnectCount(client).Should().Be(1);
    }

    [Test]
    public async Task Caller_cancel_after_the_registration_expired_leaves_the_managed_reconnect_running()
    {
        var secondConnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("refused");

                secondConnectEntered.TrySetResult();
                await releaseSecondConnect.Task;
                await connected.Handler!(ConnectedArgs());
                return Success();
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var managedConnected = 0;
        var managedDisconnects = 0;
        sut.ConnectedAsync += _ => { Interlocked.Increment(ref managedConnected); return Task.CompletedTask; };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref managedDisconnects); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(BuildOptions(TimeSpan.FromMilliseconds(30)), cts.Token);
        await secondConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        releaseSecondConnect.TrySetResult();

        await WaitUntilAsync(() => Volatile.Read(ref managedConnected) == 1);
        ConnectCount(client).Should().Be(2,
            "a caller cancel after the connect operation finished must not stop the scheduled reconnect");
        Volatile.Read(ref managedConnected).Should().Be(1,
            "the scheduled reconnect still notifies its connected subscribers");
        Volatile.Read(ref managedDisconnects).Should().Be(0,
            "a caller cancel after the connect operation finished must not tear the session down");
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

    // Parks the reconnect loop on its first ownership checkpoint, so the cancellation can be
    // requested while the loop is held there. Read 1 classifies the failed connect; read 2 is
    // that checkpoint, and its synchronous prefix runs inside StartAttempt, which the test
    // therefore starts off-thread.
    private sealed class ReconnectCheckpointGate : ISessionActivityGate, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _reads;

        public TaskCompletionSource CheckpointReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsActive
        {
            get
            {
                if (Interlocked.Increment(ref _reads) == 2)
                {
                    CheckpointReached.TrySetResult();
                    _release.Wait();
                }

                return true;
            }
        }

        public CancellationToken RevocationToken => CancellationToken.None;

        public void EnsureActive() { }

        public void Revoke() { }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
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
