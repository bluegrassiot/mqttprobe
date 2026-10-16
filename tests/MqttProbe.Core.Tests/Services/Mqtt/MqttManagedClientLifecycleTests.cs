using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class MqttManagedClientLifecycleTests
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

    private static MqttClientPublishResult PublishResult() => new(null, default, null!, []);

    [Test]
    public async Task Stop_awaits_a_blocked_canceled_connect_before_returning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        // Deliberately ignores its token so the connect stays pending after the cancel.
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory(ownsClients: true);
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        await started.Task;
        cts.Cancel();

        var stop = sut.StopAsync();
        await Task.Delay(150);
        stop.IsCompleted.Should().BeFalse("Stop must await the in-flight connect teardown");
        client.DidNotReceive().Dispose();

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        (await attempt.Completion).Status.Should().Be(MqttConnectStatus.Canceled);
        client.Received(1).Dispose();
    }

    [Test]
    public async Task ConnectionSessionLifecycle_Stop_waits_for_blocked_connect_before_disposing_certificate()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory(ownsClients: true);
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var sessionState = new SessionState();
        var resource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        sessionState.ActiveCertificateResource = resource;

        var lifecycle = new ConnectionSessionLifecycle(
            sut, sessionState, Substitute.For<ICertificateSessionQuarantine>(),
            NullLogger<ConnectionSessionLifecycle>.Instance);

        using var cts = new CancellationTokenSource();
        _ = sut.StartAttempt(BuildOptions(), cts.Token);
        await started.Task;
        cts.Cancel();

        var stop = lifecycle.StopActiveConnectionAsync();
        await Task.Delay(150);
        stop.IsCompleted.Should().BeFalse("Stop must not return while the connect cleanup is still running");
        resource.Certificate.Should().NotBeNull("the certificate must survive until the connect cleanup finishes");

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        resource.Certificate.Should().BeNull("the certificate is disposed only after Stop completes");
        sessionState.CertificateSessionFaulted.Should().BeFalse();
    }

    [Test]
    public async Task Pre_canceled_token_with_owning_factory_settles_canceled_and_disposes_once()
    {
        var client = Substitute.For<IMqttClient>();
        var factory = new QueueClientFactory(ownsClients: true);
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);

        await sut.DisposeAsync();
        client.Received(1).Dispose();
    }

    [Test]
    public async Task Adopted_client_rejects_a_second_generation()
    {
        var client = Substitute.For<IMqttClient>();
        await using var sut = new MqttManagedClient(client);
        await sut.StartAsync(BuildOptions());

        var act = () => sut.StartAttempt(BuildOptions());
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public async Task Blocked_publish_from_a_retired_generation_cannot_consume_a_new_generations_message()
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedTopics = new List<string>();

        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        clientA.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (publishedTopics) publishedTopics.Add(ci.ArgAt<MqttApplicationMessage>(0).Topic);
                publishStarted.TrySetResult();
                await releasePublish.Task;
                return PublishResult();
            });

        var clientB = Substitute.For<IMqttClient>();
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await sut.EnqueueAsync(new MqttApplicationMessageBuilder().WithTopic("A/queued").Build());
        clientA.IsConnected.Returns(true);

        // Drive the raw Connected on a pool thread: the drain inside the handler blocks mid-publish.
        var raise = Task.Run(() =>
            clientA.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs()));
        await publishStarted.Task;

        cts.Cancel();
        _ = sut.StartAttempt(BuildOptions());
        await sut.EnqueueAsync(new MqttApplicationMessageBuilder().WithTopic("B/queued").Build());

        releasePublish.TrySetResult();
        await raise.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        publishedTopics.Should().NotContain("B/queued");
    }

    [Test]
    public async Task Reconnect_wrapped_oce_after_retirement_does_not_raise_failure()
    {
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectCount = 0;

        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref connectCount) == 1)
                    return Task.FromException<MqttClientConnectResult>(new InvalidOperationException("first attempt"));

                return ReconnectAsync();
            });

        async Task<MqttClientConnectResult> ReconnectAsync()
        {
            reconnectStarted.TrySetResult();
            await releaseReconnect.Task;
            throw new InvalidOperationException("wrapped", new OperationCanceledException());
        }

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };

        _ = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(10)));
        await reconnectStarted.Task;

        var stop = sut.StopAsync();
        releaseReconnect.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref failures).Should().Be(1, "only the initial real failure is reported, not a wrapped OCE after retirement");
    }

    [Test]
    public async Task Success_is_not_reported_when_the_gate_is_revoked_during_connect()
    {
        var client = Substitute.For<IMqttClient>();
        var gate = new RevocableSessionActivityGate();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                gate.Revoke();
                return Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success });
            });

        await using var sut = new MqttManagedClient(client, gate: gate);
        var connectedRaised = false;
        sut.ConnectedAsync += _ => { connectedRaised = true; return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        connectedRaised.Should().BeFalse("a gate-revoked connect must not settle or report success");
    }

    [Test]
    public async Task Failure_is_not_reported_when_a_subscriber_retires_during_outcome_notification()
    {
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<MqttClientConnectResult>(new InvalidOperationException("boom")));

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        sut.ConnectingFailedAsync += _ => { Interlocked.Increment(ref failures); return Task.CompletedTask; };
        sut.ConnectAttemptCompletedAsync += async _ => await sut.StopAsync();

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);
        await WaitUntilAsync(() => !sut.IsStarted);
        await Task.Delay(100);

        Volatile.Read(ref failures).Should().Be(0, "a retired attempt must not report a legacy failure");
    }

    [Test]
    public async Task Outcome_subscriber_awaiting_stop_does_not_deadlock_on_a_successful_connect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await finish.Task;
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var stopCompleted = 0;
        sut.ConnectAttemptCompletedAsync += async _ =>
        {
            await sut.StopAsync();
            Interlocked.Increment(ref stopCompleted);
        };

        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task;
        finish.TrySetResult();

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref stopCompleted) == 1, 5000);
        Volatile.Read(ref stopCompleted).Should().Be(1, "the outcome subscriber's Stop must complete");
    }

    [Test]
    public async Task Outcome_subscriber_awaiting_stop_does_not_deadlock_on_a_failed_connect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await finish.Task;
                throw new InvalidOperationException("boom");
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var stopCompleted = 0;
        sut.ConnectAttemptCompletedAsync += async _ =>
        {
            await sut.StopAsync();
            Interlocked.Increment(ref stopCompleted);
        };

        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task;
        finish.TrySetResult();

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);
        await WaitUntilAsync(() => Volatile.Read(ref stopCompleted) == 1, 5000);
        Volatile.Read(ref stopCompleted).Should().Be(1, "the outcome subscriber's Stop must complete");
    }

    [Test]
    public async Task Legacy_connected_subscriber_awaiting_stop_does_not_deadlock()
    {
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                client.IsConnected.Returns(true);
                client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
                return Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success });
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var connected = 0;
        var stopCompleted = 0;
        sut.ConnectedAsync += async _ =>
        {
            Interlocked.Increment(ref connected);
            await sut.StopAsync();
            Interlocked.Increment(ref stopCompleted);
        };

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        await WaitUntilAsync(() => Volatile.Read(ref stopCompleted) == 1, 5000);

        Volatile.Read(ref connected).Should().Be(1);
        Volatile.Read(ref stopCompleted).Should().Be(1, "the legacy Connected subscriber's Stop must complete");
    }

    [Test]
    public async Task Explicit_stop_of_a_connected_generation_raises_disconnect_and_state_once()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        await using var sut = new MqttManagedClient(client);

        var disconnected = 0;
        var stateChanged = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateChanged); return Task.CompletedTask; };

        await sut.StartAsync(BuildOptions());
        await sut.StopAsync();

        Volatile.Read(ref disconnected).Should().Be(1, "an explicit Stop of a connected client reports one disconnect");
        Volatile.Read(ref stateChanged).Should().Be(1);
    }

    [Test]
    public async Task Stop_of_a_never_connected_generation_is_silent()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task;

        var stop = sut.StopAsync();
        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        Volatile.Read(ref disconnected).Should().Be(0, "a stop before the client ever connected is silent");
    }

    [Test]
    public async Task Cancel_of_a_pending_never_connected_attempt_emits_no_disconnect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1));
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var disconnected = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };

        using var cts = new CancellationTokenSource();
        var attempt = sut.StartAttempt(BuildOptions(), cts.Token);
        await started.Task;
        cts.Cancel();

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        Volatile.Read(ref disconnected).Should().Be(0);
    }

    [Test]
    public async Task Explicit_stop_does_not_suppress_a_real_disconnect_before_it()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        await using var sut = new MqttManagedClient(client);

        var disconnected = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };

        await sut.StartAsync(BuildOptions(TimeSpan.FromMinutes(5)));
        client.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());
        await WaitUntilAsync(() => Volatile.Read(ref disconnected) >= 1);

        await sut.StopAsync();

        Volatile.Read(ref disconnected).Should().BeGreaterThanOrEqualTo(1,
            "the real disconnect is reported; Stop adds at most its deliberate notification");
    }

    [Test]
    public async Task StopActiveConnectionAsync_during_auto_reconnect_cancels_retries_and_allows_a_later_connect()
    {
        var connectCalls = 0;
        var reconnectInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientA = Substitute.For<IMqttClient>();
        clientA.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref connectCalls) == 1
                ? Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success })
                : ReconnectAttemptAsync());

        async Task<MqttClientConnectResult> ReconnectAttemptAsync()
        {
            reconnectInFlight.TrySetResult();
            await releaseReconnect.Task;
            return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
        }

        var clientB = Substitute.For<IMqttClient>();
        clientB.IsConnected.Returns(true);
        clientB.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));

        var factory = new QueueClientFactory();
        factory.Enqueue(clientA);
        factory.Enqueue(clientB);
        await using var sut = new MqttManagedClient(factory, null);

        var lifecycle = new ConnectionSessionLifecycle(
            sut, new SessionState(), Substitute.For<ICertificateSessionQuarantine>(),
            NullLogger<ConnectionSessionLifecycle>.Instance);

        var disconnected = 0;
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(20)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        clientA.IsConnected.Returns(false);
        clientA.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());
        await reconnectInFlight.Task;

        var beforeStop = Volatile.Read(ref disconnected);
        var stop = lifecycle.StopActiveConnectionAsync();
        releaseReconnect.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref connectCalls).Should().Be(2, "the pending reconnect must not retry after Stop");
        sut.IsStarted.Should().BeFalse();
        sut.IsConnected.Should().BeFalse();
        Volatile.Read(ref disconnected).Should().Be(beforeStop + 1, "explicit Stop still reports a disconnect");

        var retry = sut.StartAttempt(BuildOptions());
        (await retry.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);
        sut.IsConnected.Should().BeTrue();
    }

    [Test]
    public async Task Stop_does_not_deadlock_when_a_disconnected_subscriber_awaits_stop()
    {
        var client = Substitute.For<IMqttClient>();
        client.IsConnected.Returns(true);
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success }));
        await using var sut = new MqttManagedClient(client);
        await sut.StartAsync(BuildOptions());

        var innerStops = 0;
        sut.DisconnectedAsync += async _ =>
        {
            await sut.StopAsync();
            Interlocked.Increment(ref innerStops);
        };

        await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref innerStops).Should().Be(1);
    }

    [Test]
    public async Task Reconnect_failure_subscriber_awaiting_stop_does_not_deadlock()
    {
        var connectCalls = 0;
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<MqttClientConnectResult>(
                new InvalidOperationException(Interlocked.Increment(ref connectCalls) == 1 ? "initial" : "reconnect")));

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var failures = 0;
        var stopCompleted = 0;
        sut.ConnectingFailedAsync += async _ =>
        {
            if (Interlocked.Increment(ref failures) == 2)
            {
                await sut.StopAsync();
                Interlocked.Increment(ref stopCompleted);
            }
        };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(50)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);

        await WaitUntilAsync(() => Volatile.Read(ref stopCompleted) == 1, 5000);
        Volatile.Read(ref stopCompleted).Should().Be(1);
    }

    [Test]
    public async Task Stop_reports_a_disconnect_after_initial_failure_then_reconnect_success()
    {
        var connectCalls = 0;
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref connectCalls) == 1)
                    return Task.FromException<MqttClientConnectResult>(new InvalidOperationException("initial"));

                client.IsConnected.Returns(true);
                client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
                return Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success });
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var connected = 0;
        var disconnected = 0;
        sut.ConnectedAsync += _ => { Interlocked.Increment(ref connected); return Task.CompletedTask; };
        sut.DisconnectedAsync += _ => { Interlocked.Increment(ref disconnected); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(20)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Failed);

        await WaitUntilAsync(() => Volatile.Read(ref connected) >= 1, 5000);
        await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref disconnected).Should().Be(1, "a reconnect kept the session established, so Stop reports one disconnect");
    }

    [Test]
    public async Task Reconnect_connected_after_gate_revoke_during_resubscribe_does_not_notify()
    {
        var resubscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResubscribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectCalls = 0;

        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref connectCalls) > 1)
                {
                    client.IsConnected.Returns(true);
                    client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
                }

                return Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success });
            });
        client.SubscribeAsync(Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                resubscribeStarted.TrySetResult();
                await releaseResubscribe.Task;
                return SubscribeResult();
            });

        var gate = new RevocableSessionActivityGate();
        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null, gate);

        var connected = 0;
        sut.ConnectedAsync += _ => { Interlocked.Increment(ref connected); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions(TimeSpan.FromMilliseconds(10)));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await sut.SubscribeAsync([Filter("a/b")]);

        client.DisconnectedAsync += Raise.Event<Func<MqttClientDisconnectedEventArgs, Task>>(DisconnectedArgs());
        await resubscribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        gate.Revoke();
        releaseResubscribe.TrySetResult();
        await Task.Delay(100);

        Volatile.Read(ref connected).Should().Be(0, "a connect after gate revocation must not notify success");

        await sut.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Connected_subscriber_awaiting_stop_stops_later_subscribers_and_trailing_state()
    {
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                client.IsConnected.Returns(true);
                client.ConnectedAsync += Raise.Event<Func<MqttClientConnectedEventArgs, Task>>(ConnectedArgs());
                return Task.FromResult(new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success });
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        await using var sut = new MqttManagedClient(factory, null);

        var secondSubscriberRan = 0;
        var stateCount = 0;
        sut.ConnectedAsync += async _ => await sut.StopAsync();
        sut.ConnectedAsync += _ => { Interlocked.Increment(ref secondSubscriberRan); return Task.CompletedTask; };
        sut.ConnectionStateChangedAsync += _ => { Interlocked.Increment(ref stateCount); return Task.CompletedTask; };

        var attempt = sut.StartAttempt(BuildOptions());
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Connected);

        await WaitUntilAsync(() => Volatile.Read(ref stateCount) >= 1, 5000);
        await Task.Delay(50);

        Volatile.Read(ref secondSubscriberRan).Should().Be(0, "a subscriber that stopped the generation must halt the rest");
        Volatile.Read(ref stateCount).Should().Be(1, "only the deliberate Stop state notification runs");
    }

    [Test]
    public async Task DisposeAsync_awaits_cleanup_started_by_a_synchronous_dispose()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        var sut = new MqttManagedClient(factory, null);

        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task;

        sut.Dispose();
        var disposeTask = sut.DisposeAsync().AsTask();
        await Task.Delay(100);
        disposeTask.IsCompleted.Should().BeFalse("DisposeAsync must await cleanup a synchronous Dispose started");

        release.TrySetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
    }

    [Test]
    public async Task Concurrent_DisposeAsync_callers_both_await_cleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var factory = new QueueClientFactory();
        factory.Enqueue(client);
        var sut = new MqttManagedClient(factory, null);

        _ = sut.StartAttempt(BuildOptions());
        await started.Task;

        var first = sut.DisposeAsync().AsTask();
        var second = sut.DisposeAsync().AsTask();
        await Task.Delay(100);
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();

        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static MqttTopicFilter Filter(string topic) =>
        new MqttTopicFilterBuilder().WithTopic(topic).Build();

    private static MqttClientSubscribeResult SubscribeResult() => new(0, [], null!, []);

    [Test]
    public async Task Owned_adopted_client_is_disposed_exactly_once_across_dispose_and_dispose_async()
    {
        var client = Substitute.For<IMqttClient>();
        var sut = new MqttManagedClient(client, ownsClient: true);

        sut.Dispose();
        await sut.DisposeAsync();

        client.Received(1).Dispose();
    }

    [Test]
    public async Task Concurrent_disposal_of_an_owned_adopted_client_disposes_it_once_and_awaits_cleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMqttClient>();
        client.ConnectAsync(Arg.Any<MqttClientOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new MqttClientConnectResult { ResultCode = MqttClientConnectResultCode.Success };
            });

        var sut = new MqttManagedClient(client, ownsClient: true);
        var attempt = sut.StartAttempt(BuildOptions());
        await started.Task;

        var disposals = Enumerable.Range(0, 8).Select(_ => sut.DisposeAsync().AsTask()).ToArray();
        sut.Dispose();
        await Task.Delay(100);

        foreach (var disposal in disposals)
            disposal.IsCompleted.Should().BeFalse("every DisposeAsync awaits the in-flight cleanup");

        release.TrySetResult();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(5));

        (await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(MqttConnectStatus.Canceled);
        client.Received(1).Dispose();
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

    private sealed class QueueClientFactory(bool ownsClients = false) : IMqttClientFactory
    {
        private readonly Queue<IMqttClient> _clients = new();

        public bool OwnsClients => ownsClients;

        public void Enqueue(IMqttClient client) => _clients.Enqueue(client);

        public IMqttClient CreateClient() => _clients.Dequeue();
    }
}
