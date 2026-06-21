using System.Text.Json;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;

namespace MqttProbe.Shared.Tests.Models.Configuration;

[TestFixture]
public class ConfigurationTests
{
    [Test]
    public void Connections_IsEmptyList_WhenNew()
    {
        var config = new AppConfiguration();
        config.Connections.Should().BeEmpty();
    }

    [Test]
    public void Connections_CanAddAndRetrieve()
    {
        var config = new AppConfiguration();
        var conn = new Connection { Name = "Test" };

        config.Connections.Add(conn);

        config.Connections.Should().HaveCount(1);
        config.Connections[0].Name.Should().Be("Test");
    }

    [Test]
    public void Serializes_ToJsonWithExpectedStructure()
    {
        var config = new AppConfiguration();
        config.Connections.Add(new Connection { Name = "MyBroker", Host = "localhost" });

        var json = JsonSerializer.Serialize(config);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("Connections", out var connections).Should().BeTrue();
        connections.GetArrayLength().Should().Be(1);
        connections[0].GetProperty("Name").GetString().Should().Be("MyBroker");
    }
}
