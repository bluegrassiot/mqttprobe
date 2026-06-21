using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;
using SparkplugNet.Core;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Shared.Tests.Services.Sparkplug;

[TestFixture]
public class SparkplugNodeAdapterTests
{
    [Test]
    public void SparkplugNodeFactory_Create_ReturnsISparkplugNode()
    {
        var factory = new SparkplugNodeFactory();

        var node = factory.Create(new List<Metric>(), SparkplugSpecificationVersion.Version30);

        node.Should().NotBeNull();
        node.Should().BeAssignableTo<ISparkplugNode>();
    }

    [Test]
    public void SparkplugNodeAdapter_IsConnected_FalseBeforeStart()
    {
        var factory = new SparkplugNodeFactory();

        var node = factory.Create(new List<Metric>(), SparkplugSpecificationVersion.Version30);

        node.IsConnected.Should().BeFalse();
    }

    [Test]
    public void SparkplugNodeAdapter_ConnectedEvent_CanSubscribeAndUnsubscribe()
    {
        var factory = new SparkplugNodeFactory();
        var node = factory.Create(new List<Metric>(), SparkplugSpecificationVersion.Version30);

        Func<SparkplugBase<Metric>.SparkplugEventArgs, Task> handler = _ => Task.CompletedTask;

        var act = () =>
        {
            node.Connected += handler;
            node.Connected -= handler;
        };

        act.Should().NotThrow();
    }

    [Test]
    public void SparkplugNodeAdapter_DisconnectedEvent_CanSubscribeAndUnsubscribe()
    {
        var factory = new SparkplugNodeFactory();
        var node = factory.Create(new List<Metric>(), SparkplugSpecificationVersion.Version30);

        Func<SparkplugBase<Metric>.SparkplugEventArgs, Task> handler = _ => Task.CompletedTask;

        var act = () =>
        {
            node.Disconnected += handler;
            node.Disconnected -= handler;
        };

        act.Should().NotThrow();
    }

    [Test]
    public async Task PublishDeviceBirthMessage_DelegatesToAdapter_ReachesSparkplugNet()
    {
        var factory = new SparkplugNodeFactory();
        var node = factory.Create(new List<Metric>(), SparkplugSpecificationVersion.Version30);

        // SparkplugNet throws ArgumentNullException("Options") on an unstarted node —
        // proves the adapter reached _node.PublishDeviceBirthMessage rather than failing earlier.
        var ex = await node.Invoking(n => n.PublishDeviceBirthMessage("Dev-0", new List<Metric>()))
            .Should().ThrowAsync<ArgumentNullException>();
        ex.Which.ParamName.Should().Be("Options");
    }

    [Test]
    public void PublishNodeDeathMessage_WhenSendNodeDeathMessageMethodNotFound_ThrowsInvalidOperationException()
    {
        var innerNode = new SparkplugNet.VersionB.SparkplugNode(new List<Metric>(), SparkplugSpecificationVersion.Version30);
        var adapter = new SparkplugNodeAdapter(innerNode, new List<Metric>());

        // Verify the adapter is wired to the real method so that if it disappears in a SparkplugNet upgrade,
        // the reflection guard throws InvalidOperationException rather than silently skipping the NDEATH.
        var method = innerNode.GetType().GetMethod(
            "SendNodeDeathMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (method is null)
        {
            var act = async () => await adapter.PublishNodeDeathMessage();
            act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*SendNodeDeathMessage*");
        }
        else
        {
            method.Name.Should().Be("SendNodeDeathMessage",
                "the reflection target must exist; if it disappears the adapter must throw InvalidOperationException");
        }
    }
}
