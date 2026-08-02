using Microsoft.Extensions.Logging;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Tests.Services.Security.TestHelpers;

namespace MqttProbe.Tests.Services.Mqtt;

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
}
