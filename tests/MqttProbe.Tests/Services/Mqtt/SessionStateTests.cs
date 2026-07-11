using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Services.Mqtt;

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
}
