using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class ConnectCancellationIntegrationTests
{
    private static MtlsBrokerFixture? _broker;

    [OneTimeSetUp]
    public async Task Setup() => _broker = await MtlsBrokerFixture.StartAsync();

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_broker is not null)
            await _broker.DisposeAsync();
    }

    [Test]
    public async Task Canceled_pending_start_is_silent_and_a_retry_connects()
    {
        // Accepts TCP but never speaks MQTT, so the connect stays genuinely in flight.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stallPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        var acceptedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var acceptCts = new CancellationTokenSource();
        var acceptLoop = AcceptAndHoldAsync(listener, accepted, acceptedSignal, acceptCts.Token);

        using var sut = new MqttManagedClient();
        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };

        try
        {
            var stallOptions = new MqttManagedClientOptions
            {
                ClientOptions = new MqttClientOptionsBuilder()
                    .WithTcpServer("127.0.0.1", stallPort)
                    .WithClientId($"stall-{Guid.NewGuid():N}")
                    .Build(),
                AutoReconnectDelay = TimeSpan.FromMinutes(5)
            };

            using var cts = new CancellationTokenSource();
            var attempt = sut.StartAttempt(stallOptions, cts.Token);
            await acceptedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();

            var outcome = await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            outcome.Status.Should().Be(MqttConnectStatus.Canceled);
            Volatile.Read(ref failures).Should().Be(0, "a canceled attempt is not a connect failure");
        }
        finally
        {
            acceptCts.Cancel();
            listener.Stop();
            foreach (var client in accepted)
                client.Dispose();
            try { await acceptLoop; } catch { /* listener shutdown */ }
        }

        var retry = sut.StartAttempt(BuildTlsConnectOptions("retry"));
        (await retry.Completion.WaitAsync(TimeSpan.FromSeconds(15))).Status.Should().Be(MqttConnectStatus.Connected);
        sut.IsConnected.Should().BeTrue();

        await sut.StopAsync();
    }

    [Test]
    public async Task Superseding_generation_against_broker_leaves_no_stale_events_and_new_generation_is_live()
    {
        using var sut = new MqttManagedClient();

        var failures = 0;
        var disconnected = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };

        var topic = $"cancel/supersede/{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic == topic)
                received.TrySetResult(args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty);
            return Task.CompletedTask;
        };

        var first = sut.StartAttempt(BuildTlsConnectOptions("first"));
        (await first.Completion.WaitAsync(TimeSpan.FromSeconds(15))).Status.Should().Be(MqttConnectStatus.Connected);

        var second = sut.StartAttempt(BuildTlsConnectOptions("second"));
        (await second.Completion.WaitAsync(TimeSpan.FromSeconds(15))).Status.Should().Be(MqttConnectStatus.Connected);

        await sut.SubscribeAsync([new MqttTopicFilterBuilder()
            .WithTopic(topic)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build()]);
        await PublishAsync(topic, "after-supersede");

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("after-supersede");

        Volatile.Read(ref failures).Should().Be(0);
        Volatile.Read(ref disconnected).Should().Be(0, "retiring a generation must not surface a disconnect");
        sut.IsConnected.Should().BeTrue();

        await sut.StopAsync();
    }

    private static async Task AcceptAndHoldAsync(
        TcpListener listener, List<TcpClient> accepted, TaskCompletionSource acceptedSignal, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            accepted.Add(client);
            acceptedSignal.TrySetResult();
        }
    }

    private static async Task PublishAsync(string topic, string payload)
    {
        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(BuildTlsConnectOptions("publisher").ClientOptions);

        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        await publisher.DisconnectAsync(new MqttClientDisconnectOptions());
    }

    private static MqttManagedClientOptions BuildTlsConnectOptions(string clientIdSuffix)
    {
        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker!.Port)
            .WithClientId($"cancel-{clientIdSuffix}-{Guid.NewGuid():N}")
            .WithCleanSession()
            .WithTlsOptions(o => o
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13) // DevSkim: ignore DS440020,DS112836,DS440001
                .WithClientCertificates(new X509Certificate2Collection(
                    X509CertificateLoader.LoadPkcs12(
                        _broker.ClientPfxBytes, _broker.ClientPfxPassword,
                        X509KeyStorageFlags.Exportable)))
                .WithCertificateValidationHandler(ctx =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.ExtraStore.Add(_broker.CaCert);
                    chain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority
                        | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                        | X509VerificationFlags.IgnoreEndRevocationUnknown
                        | X509VerificationFlags.IgnoreRootRevocationUnknown;
                    return chain.Build((X509Certificate2)ctx.Certificate);
                }))
            .Build();

        return new MqttManagedClientOptions
        {
            ClientOptions = clientOptions,
            AutoReconnectDelay = TimeSpan.FromSeconds(60)
        };
    }
}
