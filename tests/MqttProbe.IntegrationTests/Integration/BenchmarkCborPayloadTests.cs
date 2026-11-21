extern alias benchmarks;

using System.Reflection;
using System.Text;
using System.Text.Json;
using benchmarks::MqttProbe.Benchmarks;
using benchmarks::MqttProbe.Benchmarks.Models;
using MQTTnet;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Registry;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class BenchmarkCborPayloadTests
{
    private static MqttApplicationMessageReceivedEventArgs MakeArgs(byte[] payload)
    {
        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic("benchmarks/payloads/cbor")
            .WithPayload(payload)
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket { Topic = "benchmarks/payloads/cbor" };
        return new MqttApplicationMessageReceivedEventArgs("test-client", appMsg, packet, null);
    }

    private static PluginRegistry BuildRegistry()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        return builder.Build();
    }

    [Test]
    public void CborSample_DetectsAsCborAndDecodesToJsonFields()
    {
        var registry = BuildRegistry();
        var args = MakeArgs(PayloadFactory.CreateSample(PayloadFormat.Cbor));

        var detector = registry.FindDetector(args);

        detector.Should().NotBeNull();
        detector.FormatId.Should().Be("cbor");

        var decoded = registry.FindDecoder("cbor")!.Decode(args);
        decoded.IsFailure.Should().BeFalse();

        using var cborDocument = JsonDocument.Parse(decoded.DisplayText);
        using var jsonDocument = JsonDocument.Parse(
            Encoding.UTF8.GetString(PayloadFactory.CreateSample(PayloadFormat.Json)));

        foreach (var field in new[] { "temperature", "pressure", "flowRate" })
        {
            cborDocument.RootElement.GetProperty(field).GetDouble()
                .Should().Be(jsonDocument.RootElement.GetProperty(field).GetDouble());
        }

        cborDocument.RootElement.GetProperty("status").GetString()
            .Should().Be(jsonDocument.RootElement.GetProperty("status").GetString());
    }

    [Test]
    public void CborFormat_IsLastAndUsesExpectedTopic()
    {
        Enum.GetNames<PayloadFormat>().Last().Should().Be("Cbor");

        var getTopic = typeof(PayloadPublisher).GetMethod(
            "GetTopic", BindingFlags.Static | BindingFlags.NonPublic);

        getTopic.Should().NotBeNull();
        getTopic!.Invoke(null, [PayloadFormat.Cbor]).Should().Be("benchmarks/payloads/cbor");
    }
}
