using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Tests.Services.Mqtt;

[TestFixture]
public class SessionStateTests
{
    private SessionState _sessionState = null!;

    [SetUp]
    public void Setup() => _sessionState = new SessionState();

    [Test]
    public void SelectedConnection_DefaultsToNewConnection()
    {
        _sessionState.SelectedConnection.Should().NotBeNull();
        _sessionState.SelectedConnection.Name.Should().Be("New Connection");
    }

    [Test]
    public void SelectedConnection_CanBeSetAndRetrieved()
    {
        var conn = new Connection { Name = "MyBroker", Host = "broker.local" };

        _sessionState.SelectedConnection = conn;

        _sessionState.SelectedConnection.Name.Should().Be("MyBroker");
        _sessionState.SelectedConnection.Host.Should().Be("broker.local");
    }

    [Test]
    public void SelectedConnection_WhenChanged_RaisesSelectedConnectionChanged()
    {
        var conn = new Connection { Name = "MyBroker", Host = "broker.local" };
        Connection? notified = null;
        _sessionState.SelectedConnectionChanged += changed => notified = changed;

        _sessionState.SelectedConnection = conn;

        notified.Should().BeSameAs(conn);
    }

    [Test]
    public void SelectedConnection_WhenAssignedEquivalentValue_DoesNotRaiseSelectedConnectionChanged()
    {
        var conn = new Connection { Name = "MyBroker", Host = "broker.local", ClientId = "client" };
        _sessionState.SelectedConnection = conn;
        var notificationCount = 0;
        _sessionState.SelectedConnectionChanged += _ => notificationCount++;

        _sessionState.SelectedConnection = conn.Clone();

        notificationCount.Should().Be(0);
    }

    [Test]
    public void SelectedConnection_CanBeReplacedWithDifferentConnection()
    {
        var first = new Connection { Name = "First" };
        var second = new Connection { Name = "Second" };

        _sessionState.SelectedConnection = first;
        _sessionState.SelectedConnection = second;

        _sessionState.SelectedConnection.Name.Should().Be("Second");
    }

    [Test]
    public void CaptureSelectedConnection_AdvancesRevisionOnlyForAcceptedTransitions()
    {
        var first = new Connection { Name = "First" };
        _sessionState.SelectedConnection = first;
        var (_, initialRevision) = _sessionState.CaptureSelectedConnection();

        _sessionState.SelectedConnection = first.Clone();
        var (_, unchangedRevision) = _sessionState.CaptureSelectedConnection();

        var second = new Connection { Name = "Second" };
        _sessionState.SelectedConnection = second;
        var (captured, changedRevision) = _sessionState.CaptureSelectedConnection();

        unchangedRevision.Should().Be(initialRevision, "an equivalent assignment is not a transition");
        changedRevision.Should().Be(initialRevision + 1);
        captured.Should().BeSameAs(second);
    }

    [Test]
    public void TryExecuteForSelectedConnection_ExecutesOnlyForTheCapturedRevision()
    {
        var first = new Connection { Name = "First" };
        _sessionState.SelectedConnection = first;
        var (_, revision) = _sessionState.CaptureSelectedConnection();
        var executions = 0;

        _sessionState.TryExecuteForSelectedConnection(
            first.Id, revision, () => ++executions, out var result).Should().BeTrue();
        result.Should().Be(1);

        _sessionState.SelectedConnection = new Connection { Name = "Second" };
        _sessionState.TryExecuteForSelectedConnection(
            first.Id, revision, () => ++executions, out var staleResult).Should().BeFalse();

        executions.Should().Be(1);
        staleResult.Should().Be(0);
    }

    [Test]
    public void SelectedConnectionChanged_HandlerCanReenterSelectionWithoutDeadlock()
    {
        var first = new Connection { Name = "First" };
        var second = new Connection { Name = "Second" };
        var third = new Connection { Name = "Third" };
        _sessionState.SelectedConnection = first;
        long observedRevision = -1;

        _sessionState.SelectedConnectionChanged += changed =>
        {
            if (ReferenceEquals(changed, second))
                _sessionState.SelectedConnection = third;

            observedRevision = _sessionState.CaptureSelectedConnection().Revision;
        };

        _sessionState.SelectedConnection = second;

        _sessionState.SelectedConnection.Should().BeSameAs(third);
        observedRevision.Should().Be(3);
    }

    [Test]
    public void TakeActiveCertificateResource_ReturnsAndClearsTheActiveResource()
    {
        var resource = new CertificateSessionResource();
        _sessionState.ActiveCertificateResource = resource;

        _sessionState.TakeActiveCertificateResource().Should().BeSameAs(resource);
        _sessionState.ActiveCertificateResource.Should().BeNull();
        _sessionState.TakeActiveCertificateResource().Should().BeNull();
    }

    [Test]
    public void TakeActiveCertificateResource_WithExpectedResource_ClaimsThatIdentityOnly()
    {
        var resource = new CertificateSessionResource();
        _sessionState.ActiveCertificateResource = resource;

        _sessionState.TakeActiveCertificateResource(resource).Should().BeSameAs(resource);
        _sessionState.ActiveCertificateResource.Should().BeNull();
    }

    [Test]
    public void TakeActiveCertificateResource_WithExpectedResource_ReturnsNullForAReplacement()
    {
        var superseded = new CertificateSessionResource();
        var replacement = new CertificateSessionResource();
        _sessionState.ActiveCertificateResource = replacement;

        _sessionState.TakeActiveCertificateResource(superseded).Should().BeNull();
        _sessionState.ActiveCertificateResource.Should().BeSameAs(replacement);
    }

    [Test]
    public void TakeActiveCertificateResource_WhenNothingIsActive_ReturnsNull()
    {
        _sessionState.TakeActiveCertificateResource().Should().BeNull();
        _sessionState.TakeActiveCertificateResource(new CertificateSessionResource()).Should().BeNull();
    }

    [Test]
    public void TakeActiveCertificateResource_WithVersion_CapturesThePublicationWithoutAdvancingIt()
    {
        var resource = new CertificateSessionResource();
        _sessionState.ActiveCertificateResource = resource;

        var taken = _sessionState.TakeActiveCertificateResource(resource, out var version);

        taken.Should().BeSameAs(resource);
        _sessionState.TryMarkCertificateSessionFaulted(version).Should().BeTrue(
            "detaching a claimed certificate is not a new publication");
        _sessionState.CertificateSessionFaulted.Should().BeTrue();
    }

    [Test]
    public void TryMarkCertificateSessionFaulted_AfterAReplacementPublication_IsRejected()
    {
        var claimed = new CertificateSessionResource();
        _sessionState.ActiveCertificateResource = claimed;
        _sessionState.TakeActiveCertificateResource(claimed, out var version);

        _sessionState.ActiveCertificateResource = new CertificateSessionResource();

        _sessionState.TryMarkCertificateSessionFaulted(version).Should().BeFalse();
        _sessionState.CertificateSessionFaulted.Should().BeFalse();
    }

    [Test]
    public void TryMarkCertificateSessionFaulted_AfterRepublishingTheSameReference_IsRejected()
    {
        var claimed = new CertificateSessionResource();
        _sessionState.ActiveCertificateResource = claimed;
        _sessionState.TakeActiveCertificateResource(claimed, out var version);

        _sessionState.ActiveCertificateResource = claimed;

        _sessionState.TryMarkCertificateSessionFaulted(version).Should().BeFalse(
            "every publication advances the version, even one that republishes the same reference");
        _sessionState.CertificateSessionFaulted.Should().BeFalse();
    }

    [Test]
    public void TryMarkCertificateSessionFaulted_AfterAnExplicitNullPublication_IsRejected()
    {
        var claimed = new CertificateSessionResource();
        _sessionState.ActiveCertificateResource = claimed;
        _sessionState.TakeActiveCertificateResource(claimed, out var version);

        _sessionState.ActiveCertificateResource = null;

        _sessionState.TryMarkCertificateSessionFaulted(version).Should().BeFalse();
        _sessionState.CertificateSessionFaulted.Should().BeFalse();
    }
}
