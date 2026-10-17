using Microsoft.Extensions.Logging;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class ConnectionSessionLifecycleTests
{
    private IMqttManagedClient _mockMqttClient = null!;
    private ISessionState _sessionState = null!;
    private ICertificateSessionQuarantine _mockQuarantine = null!;
    private ILogger<ConnectionSessionLifecycle> _mockLogger = null!;
    private ConnectionSessionLifecycle _lifecycle = null!;

    [SetUp]
    public void Setup()
    {
        _mockMqttClient = Substitute.For<IMqttManagedClient>();
        _sessionState = new SessionState();
        _mockQuarantine = Substitute.For<ICertificateSessionQuarantine>();
        _mockLogger = Substitute.For<ILogger<ConnectionSessionLifecycle>>();
        _lifecycle = new ConnectionSessionLifecycle(
            _mockMqttClient, _sessionState, _mockQuarantine, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        (_mockMqttClient as IDisposable)?.Dispose();
    }

    [Test]
    public async Task StopActiveConnectionAsync_Success_DisposesCertResource()
    {
        var resource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        _sessionState.ActiveCertificateResource = resource;

        _mockMqttClient.StopAsync().Returns(Task.CompletedTask);

        await _lifecycle.StopActiveConnectionAsync();

        _sessionState.ActiveCertificateResource.Should().BeNull();
        _sessionState.CertificateSessionFaulted.Should().BeFalse();
        resource.Certificate.Should().BeNull();
    }

    [Test]
    public async Task StopActiveConnectionAsync_StopFails_QuarantinesResource()
    {
        var resource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        _sessionState.ActiveCertificateResource = resource;

        var exception = new Exception("broker unreachable");
        _mockMqttClient.StopAsync().Returns(Task.FromException(exception));

        var act = () => _lifecycle.StopActiveConnectionAsync();
        await act.Should().ThrowAsync<Exception>().WithMessage("*broker unreachable*");

        _sessionState.ActiveCertificateResource.Should().BeNull();
        _sessionState.CertificateSessionFaulted.Should().BeTrue();
        _mockQuarantine.Received(1).Quarantine(
            resource,
            Arg.Is<string>(s => s != null && s.Contains("broker unreachable")));
    }

    [Test]
    public async Task StopActiveConnectionAsync_StopFails_SetsFaultFlag()
    {
        _sessionState.ActiveCertificateResource = null;
        _mockMqttClient.StopAsync().Returns(Task.FromException(new Exception("fail")));

        try { await _lifecycle.StopActiveConnectionAsync(); } catch { }

        _sessionState.CertificateSessionFaulted.Should().BeTrue();
    }

    [Test]
    public async Task StopActiveConnectionAsync_StopFails_PropagatesException()
    {
        _sessionState.ActiveCertificateResource = null;
        var exception = new InvalidOperationException("transport error");
        _mockMqttClient.StopAsync().Returns(Task.FromException(exception));

        var act = () => _lifecycle.StopActiveConnectionAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*transport error*");
    }

    [Test]
    public async Task StopActiveConnectionAsync_NoActiveCert_DoesNotThrow()
    {
        _sessionState.ActiveCertificateResource = null;
        _mockMqttClient.StopAsync().Returns(Task.CompletedTask);

        var act = () => _lifecycle.StopActiveConnectionAsync();
        await act.Should().NotThrowAsync();

        _mockQuarantine.DidNotReceive().Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Any<string>());
    }

    [Test]
    public async Task StopActiveConnectionAsync_Success_RaisesActiveConnectionStopped()
    {
        _mockMqttClient.StopAsync().Returns(Task.CompletedTask);
        var raised = false;
        _lifecycle.ActiveConnectionStopped += () => raised = true;

        await _lifecycle.StopActiveConnectionAsync();

        raised.Should().BeTrue();
    }

    [Test]
    public async Task StopActiveConnectionAsync_StopFails_DoesNotRaiseActiveConnectionStopped()
    {
        _mockMqttClient.StopAsync().Returns(Task.FromException(new Exception("fail")));
        var raised = false;
        _lifecycle.ActiveConnectionStopped += () => raised = true;

        try { await _lifecycle.StopActiveConnectionAsync(); } catch { }

        raised.Should().BeFalse();
    }

    [Test]
    public async Task StopActiveConnectionAsync_StopFails_NoActiveCert_SkipsQuarantine()
    {
        _sessionState.ActiveCertificateResource = null;
        _mockMqttClient.StopAsync().Returns(Task.FromException(new Exception("fail")));

        try { await _lifecycle.StopActiveConnectionAsync(); } catch { }

        _mockQuarantine.DidNotReceive().Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Any<string>());
        _sessionState.CertificateSessionFaulted.Should().BeTrue();
    }

    [Test]
    public async Task StopAttemptAsync_BlockedTeardown_ReleasesClaimedCertOnlyAfterCleanup_AndReplacementSurvives()
    {
        var aResource = new CertificateSessionResource();
        using var certA = TestCertFactory.CreateRsaCert();
        aResource.Set(certA);
        _sessionState.ActiveCertificateResource = aResource;

        var teardownGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockMqttClient.StopAttemptAsync(Arg.Any<MqttConnectAttemptId>(), Arg.Any<CancellationToken>())
            .Returns(_ => teardownGate.Task);

        var stop = _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(1), aResource);
        await Task.Delay(50);

        var bResource = new CertificateSessionResource();
        using var certB = TestCertFactory.CreateRsaCert();
        bResource.Set(certB);
        _sessionState.ActiveCertificateResource = bResource;

        stop.IsCompleted.Should().BeFalse("the certificate is released only after the targeted cleanup");
        aResource.Certificate.Should().NotBeNull("the claimed certificate is still held while cleanup runs");
        _sessionState.ActiveCertificateResource.Should().BeSameAs(bResource);

        teardownGate.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        aResource.Certificate.Should().BeNull("the claimed certificate is released after cleanup");
        bResource.Certificate.Should().NotBeNull("the replacement session keeps its certificate");
        _sessionState.ActiveCertificateResource.Should().BeSameAs(bResource);
        _sessionState.CertificateSessionFaulted.Should().BeFalse();
    }

    [Test]
    public async Task StopAttemptAsync_SupersededAttempt_DoesNotTakeOrReleaseTheReplacementCertificate()
    {
        var aResource = new CertificateSessionResource();
        using var certA = TestCertFactory.CreateRsaCert();
        aResource.Set(certA);

        var bResource = new CertificateSessionResource();
        using var certB = TestCertFactory.CreateRsaCert();
        bResource.Set(certB);
        _sessionState.ActiveCertificateResource = bResource;

        await _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(1), aResource);

        _sessionState.ActiveCertificateResource.Should().BeSameAs(bResource);
        bResource.Certificate.Should().NotBeNull();
        aResource.Certificate.Should().NotBeNull("a failed claim must not dispose the supplied resource");
        _mockQuarantine.DidNotReceive().Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Any<string>());
    }

    [Test]
    public async Task StopAttemptAsync_RepeatedTeardown_DoesNotDisposeTheCertificateTwice()
    {
        var resource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        _sessionState.ActiveCertificateResource = resource;

        await _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(1), resource);
        var act = () => _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(1), resource);
        await act.Should().NotThrowAsync();

        resource.Certificate.Should().BeNull();
        _sessionState.ActiveCertificateResource.Should().BeNull();
        _sessionState.CertificateSessionFaulted.Should().BeFalse();
        _mockQuarantine.DidNotReceive().Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Any<string>());
    }

    [Test]
    public async Task StopAttemptAsync_TeardownFails_QuarantinesClaimedCertAndFaults()
    {
        var resource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        _sessionState.ActiveCertificateResource = resource;

        var exception = new Exception("attempt teardown failed");
        _mockMqttClient.StopAttemptAsync(Arg.Any<MqttConnectAttemptId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(exception));

        var act = () => _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(7), resource);
        await act.Should().ThrowAsync<Exception>().WithMessage("*attempt teardown failed*");

        _sessionState.CertificateSessionFaulted.Should().BeTrue();
        _mockQuarantine.Received(1).Quarantine(
            resource,
            Arg.Is<string>(s => s != null && s.Contains("attempt teardown failed")));
    }

    [Test]
    public async Task StopAttemptAsync_TeardownFailsAfterAFailedClaim_LeavesTheReplacementSessionAlone()
    {
        var aResource = new CertificateSessionResource();
        using var certA = TestCertFactory.CreateRsaCert();
        aResource.Set(certA);

        var bResource = new CertificateSessionResource();
        using var certB = TestCertFactory.CreateRsaCert();
        bResource.Set(certB);
        _sessionState.ActiveCertificateResource = bResource;

        _mockMqttClient.StopAttemptAsync(Arg.Any<MqttConnectAttemptId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("attempt teardown failed")));

        var act = () => _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(7), aResource);
        await act.Should().ThrowAsync<Exception>();

        _sessionState.CertificateSessionFaulted.Should().BeFalse(
            "a superseded attempt must not fault the replacement session");
        _mockQuarantine.DidNotReceive().Quarantine(
            Arg.Any<CertificateSessionResource>(),
            Arg.Any<string>());
        bResource.Certificate.Should().NotBeNull();
        aResource.Certificate.Should().NotBeNull();
    }

    [Test]
    public async Task StopAttemptAsync_SupersededAttempt_DoesNotRaiseActiveConnectionStopped()
    {
        var bResource = new CertificateSessionResource();
        using var certB = TestCertFactory.CreateRsaCert();
        bResource.Set(certB);
        _sessionState.ActiveCertificateResource = bResource;

        var raised = false;
        _lifecycle.ActiveConnectionStopped += () => raised = true;

        await _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(1), null);

        raised.Should().BeFalse("stopping a scoped attempt is not a global connection stop");
        _sessionState.ActiveCertificateResource.Should().BeSameAs(bResource);
    }

    [Test]
    public async Task StopAttemptAsync_TeardownFailsAfterAReplacementPublished_DoesNotFaultTheReplacement()
    {
        var aResource = new CertificateSessionResource();
        using var certA = TestCertFactory.CreateRsaCert();
        aResource.Set(certA);
        _sessionState.ActiveCertificateResource = aResource;

        var teardownGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockMqttClient.StopAttemptAsync(Arg.Any<MqttConnectAttemptId>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await teardownGate.Task;
                throw new Exception("attempt teardown failed");
            });

        var stop = _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(1), aResource);
        await Task.Delay(50);

        var bResource = new CertificateSessionResource();
        using var certB = TestCertFactory.CreateRsaCert();
        bResource.Set(certB);
        _sessionState.ActiveCertificateResource = bResource;

        teardownGate.TrySetResult();
        var act = () => stop;
        await act.Should().ThrowAsync<Exception>().WithMessage("*attempt teardown failed*");

        _sessionState.CertificateSessionFaulted.Should().BeFalse(
            "the replacement session published after the claim and must stay usable");
        bResource.Certificate.Should().NotBeNull();
        _sessionState.ActiveCertificateResource.Should().BeSameAs(bResource);
        _mockQuarantine.Received(1).Quarantine(
            aResource,
            Arg.Is<string>(s => s != null && s.Contains("attempt teardown failed")));
    }

    [Test]
    public async Task StopAttemptAsync_TeardownFailsWithoutAReplacement_FaultsAndQuarantines()
    {
        var resource = new CertificateSessionResource();
        using var cert = TestCertFactory.CreateRsaCert();
        resource.Set(cert);
        _sessionState.ActiveCertificateResource = resource;

        _mockMqttClient.StopAttemptAsync(Arg.Any<MqttConnectAttemptId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("attempt teardown failed")));

        var act = () => _lifecycle.StopAttemptAsync(new MqttConnectAttemptId(3), resource);
        await act.Should().ThrowAsync<Exception>();

        _sessionState.CertificateSessionFaulted.Should().BeTrue(
            "no replacement published, so the failed teardown still faults the session");
        _mockQuarantine.Received(1).Quarantine(
            resource,
            Arg.Is<string>(s => s != null && s.Contains("attempt teardown failed")));
    }
}
