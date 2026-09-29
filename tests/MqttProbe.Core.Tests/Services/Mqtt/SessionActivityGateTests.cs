using MqttProbe.Core.Services.Mqtt;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class SessionActivityGateTests
{
    [Test]
    public void AlwaysActiveGate_IsActive_ReturnsTrue()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        gate.IsActive.Should().BeTrue();
    }

    [Test]
    public void AlwaysActiveGate_EnsureActive_DoesNotThrow()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        var act = () => gate.EnsureActive();
        act.Should().NotThrow();
    }

    [Test]
    public void AlwaysActiveGate_RevocationToken_IsCancellationTokenNone()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        gate.RevocationToken.Should().Be(CancellationToken.None);
    }

    [Test]
    public void AlwaysActiveGate_Revoke_DoesNotChangeIsActive()
    {
        var gate = new AlwaysActiveSessionActivityGate();
        gate.Revoke();
        gate.IsActive.Should().BeTrue();
    }

    [Test]
    public void RevocableGate_InitiallyActive()
    {
        var gate = new RevocableSessionActivityGate();
        gate.IsActive.Should().BeTrue();
        gate.RevocationToken.IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public void RevocableGate_EnsureActive_BeforeRevoke_DoesNotThrow()
    {
        var gate = new RevocableSessionActivityGate();
        var act = () => gate.EnsureActive();
        act.Should().NotThrow();
    }

    [Test]
    public void RevocableGate_Revoke_SetsIsActiveFalse()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        gate.IsActive.Should().BeFalse();
    }

    [Test]
    public void RevocableGate_Revoke_CancelsRevocationToken()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        gate.RevocationToken.IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public void RevocableGate_Revoke_EnsureActiveThrows()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        var act = () => gate.EnsureActive();
        act.Should().ThrowExactly<InvalidOperationException>();
    }

    [Test]
    public void RevocableGate_Revoke_IsIdempotent()
    {
        var gate = new RevocableSessionActivityGate();
        gate.Revoke();
        gate.Revoke();
        gate.IsActive.Should().BeFalse();
        gate.RevocationToken.IsCancellationRequested.Should().BeTrue();
    }
}
