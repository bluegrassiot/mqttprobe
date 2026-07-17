using MqttProbe.Models.Emulation;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Emulation;

namespace MqttProbe.Shared.Tests.Services.Emulation;

[TestFixture]
public class SparkplugNodeOptionsBuilderTests
{
    private static Connection TcpConnection() => new()
    {
        Name = "Test",
        Host = "broker.local",
        Port = 1883,
        Protocol = Protocol.Mqtt,
        ClientId = "primary-client",
        User = "u",
        Password = "p",
        ConnectTimeout = 15,
        ReconnectDelay = 5,
        KeepAlivePeriod = 15
    };

    private static EmulatorNodeConfig Node(string nodeId = "Press-01") => new()
    {
        NodeId = nodeId,
        GroupId = "Plant"
    };

    [Test]
    public void BuildNodeOptions_ClientId_IsNodeIdPlusSixHexSuffix()
    {
        var options = SparkplugNodeRunner.BuildNodeOptions(TcpConnection(), Node("Press-01"));

        options.ClientId.Should().StartWith("Press-01-");
        options.ClientId.Length.Should().Be("Press-01-".Length + 6);
        options.ClientId["Press-01-".Length..].Should().MatchRegex("^[0-9a-f]{6}$");
    }

    [Test]
    public void BuildNodeOptions_EdgeNodeIdentifier_RemainsBareNodeId()
    {
        var options = SparkplugNodeRunner.BuildNodeOptions(TcpConnection(), Node("Press-01"));

        options.EdgeNodeIdentifier.Should().Be("Press-01");
        options.GroupIdentifier.Should().Be("Plant");
    }

    [Test]
    public void BuildNodeOptions_TwoCalls_ProduceDifferentClientIds()
    {
        var conn = TcpConnection();
        var node = Node("Press-01");

        var id1 = SparkplugNodeRunner.BuildNodeOptions(conn, node).ClientId;
        var id2 = SparkplugNodeRunner.BuildNodeOptions(conn, node).ClientId;

        id1.Should().NotBe(id2);
    }

    [Test]
    public void BuildNodeOptions_DefaultReconnectDelay_Uses5Seconds()
    {
        var options = SparkplugNodeRunner.BuildNodeOptions(TcpConnection(), Node());

        options.ReconnectInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void BuildNodeOptions_CustomReconnectDelay_UsesConfiguredValue()
    {
        var conn = TcpConnection();
        conn.ReconnectDelay = 12;

        var options = SparkplugNodeRunner.BuildNodeOptions(conn, Node());

        options.ReconnectInterval.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Test]
    public void BuildNodeOptions_ZeroReconnectDelay_FallsBackTo5Seconds()
    {
        var conn = TcpConnection();
        conn.ReconnectDelay = 0;

        var options = SparkplugNodeRunner.BuildNodeOptions(conn, Node());

        options.ReconnectInterval.Should().Be(TimeSpan.FromSeconds(5));
    }
}
