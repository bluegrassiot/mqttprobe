using Microsoft.Extensions.Logging;
using MQTTnet;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientReconnectBarrierTests
{
    private static MqttManagedClientOptions BuildOptions() => new()
    {
        ClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("test")
            .Build(),
        AutoReconnectDelay = TimeSpan.Zero
    };

    private static MqttClientConnectResult Success() =>
        new() { ResultCode = MqttClientConnectResultCode.Success };

    private static MqttClientConnectedEventArgs ConnectedArgs() => new(new MqttClientConnectResult());

    private static MqttClientDisconnectedEventArgs DisconnectedArgs() =>
        new(true, null!, MqttClientDisconnectReason.NormalDisconnection, null!, null!, null!);

    [TestCase(true)]
    [TestCase(false)]
    public async Task Cleanup_waits_for_an_in_flight_raw_reconnect_before_disposing_client_and_certificate(bool globalStop)
    {
        var handlerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRawConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = 0;
        var isConnected = false;
        var disconnected = 0;
        var disposed = 0;
        var connectedNotifications = 0;
        var client = Substitute.For<IMqttClient>();
        var connected = CaptureConnected(client);
        var rawDisconnected = CaptureDisconnected(client);
        client.IsConnected.Returns(_ => isConnected);
        client.DisconnectAsync(Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref disconnected);
                isConnected = false;
                return Task.CompletedTask;
            });
        client.When(c => c.Dispose()).Do(_ => Interlocked.Increment(ref disposed));
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                isConnected = true;
                await connected.Handler!(ConnectedArgs());

                if (Interlocked.Increment(ref calls) == 1)
                    return Success();

                handlerReturned.TrySetResult();
                await releaseRawConnect.Task;
                return Success();
            });

        var factory = new QueueClientFactory(ownsClients: true);
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var sessionState = new SessionState();
        var quarantine = Substitute.For<ICertificateSessionQuarantine>();
        var lifecycle = new ConnectionSessionLifecycle(
            sut, sessionState, quarantine, Substitute.For<ILogger<ConnectionSessionLifecycle>>());

        var certResource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        certResource.Set(cert);
        sessionState.ActiveCertificateResource = certResource;

        Task? stop = null;
        MqttConnectAttemptId attemptId = default;
        sut.ConnectAttemptCompletedAsync += outcome =>
        {
            attemptId = outcome.AttemptId;
            return Task.CompletedTask;
        };
        sut.ConnectedAsync += _ =>
        {
            if (Interlocked.Increment(ref connectedNotifications) == 1)
                return Task.CompletedTask;

            stop = globalStop
                ? lifecycle.StopActiveConnectionAsync()
                : lifecycle.StopAttemptAsync(attemptId, certResource);
            stopStarted.TrySetResult();
            return stop;
        };

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref connectedNotifications) == 1);

        try
        {
            isConnected = false;
            _ = rawDisconnected.Handler!(DisconnectedArgs());
            await handlerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stop.Should().NotBeNull();
            stop!.IsCompleted.Should().BeFalse("cleanup waits for the reconnect that is still on the wire");
            Volatile.Read(ref disconnected).Should().Be(0, "the raw client is not disconnected under its own reconnect");
            Volatile.Read(ref disposed).Should().Be(0, "the raw client is not disposed under its own reconnect");
            certResource.Certificate.Should().NotBeNull("the certificate is released only after the reconnect returns");

            releaseRawConnect.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseRawConnect.TrySetResult();
        }

        await WaitUntilAsync(() => Volatile.Read(ref disposed) == 1);
        Volatile.Read(ref disconnected).Should().Be(1);
        Volatile.Read(ref disposed).Should().Be(1);
        certResource.Certificate.Should().BeNull("the claimed certificate is disposed once cleanup finishes");
        quarantine.DidNotReceive().Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Any<string>());
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

        public QueueClientFactory(bool ownsClients = false) => OwnsClients = ownsClients;

        public bool OwnsClients { get; }

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
