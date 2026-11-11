using System.Buffers;
using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core.Models.Emulation;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Emulation;
using MqttProbe.Core.Services.Security;
using MqttProbe.Core.Services.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.IntegrationTests.Integration;

public sealed partial class SparkplugSequenceRecoveryTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task EmulatorRebirth_RepublishesBirthsWithoutNodeDeath(bool useMetricAliases)
    {
        var groupId = $"emu-{Guid.NewGuid():N}";
        const string nodeId = "node";
        const string deviceId = "dev";
        var nbirthTopic = $"spBv1.0/{groupId}/NBIRTH/{nodeId}";
        var ndeathTopic = $"spBv1.0/{groupId}/NDEATH/{nodeId}";
        var dbirthTopic = $"spBv1.0/{groupId}/DBIRTH/{nodeId}/{deviceId}";

        var certStore = new CertificateAssetStore(
            new InMemoryEnvelopeKeyStore(),
            Path.GetTempPath(),
            Substitute.For<ILogger<CertificateAssetStore>>());
        var connection = new Connection
        {
            Name = "Emulator rebirth",
            Host = "127.0.0.1",
            Port = _broker!.Port,
            UseTls = true,
            AllowUntrustedCertificate = true,
            ClientId = "emulator-rebirth"
        };
        connection.ClientCertificateAssetId = await certStore.ImportAsync(
            connection.Id,
            new CertificateImportRequest(
                CertificateInputMode.Pfx, _broker.ClientPfxBytes, null, _broker.ClientPfxPassword));

        var config = new EmulatorNodeConfig
        {
            Type = EmulatorNodeType.SparkplugB,
            GroupId = groupId,
            NodeId = nodeId,
            UseMetricAliases = useMetricAliases,
            Devices =
            [
                new EmulatorDeviceConfig
                {
                    DeviceId = deviceId,
                    Metrics = [new EmulatorMetricConfig { Name = "Speed", Waveform = WaveformKind.Constant, ConstantValue = 5 }]
                }
            ]
        };

        var received = new ConcurrentQueue<(string Topic, Payload Payload)>();
        using var observer = new MqttClientFactory().CreateMqttClient();
        using var commander = new MqttClientFactory().CreateMqttClient();
        var runner = new SparkplugNodeRunner(
            config, new SparkplugNodeFactory(), connection, [],
            certStore, Substitute.For<ICertificateSessionQuarantine>(), Substitute.For<ILogger>());

        int Count(string topic) => received.Count(message => message.Topic == topic);

        try
        {
            await observer.ConnectAsync(BuildTlsConnectOptions("emulator-observer", _broker).ClientOptions);
            observer.ApplicationMessageReceivedAsync += args =>
            {
                var message = args.ApplicationMessage;
                received.Enqueue((message.Topic, Payload.Parser.ParseFrom(message.Payload.ToArray())));
                return Task.CompletedTask;
            };
            await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(Filter($"spBv1.0/{groupId}/#"))
                .Build());
            await commander.ConnectAsync(BuildTlsConnectOptions("emulator-commander", _broker).ClientOptions);

            await runner.StartAsync();
            await WaitForAsync(
                () => Count(nbirthTopic) == 1 && Count(dbirthTopic) >= 1,
                TimeSpan.FromSeconds(15),
                "the emulator did not publish its initial births");
            var deviceBirthsAfterStart = Count(dbirthTopic);

            await commander.PublishAsync(Command($"spBv1.0/{groupId}/NCMD/{nodeId}", "Node Control/Rebirth"));
            await WaitForAsync(
                () => Count(nbirthTopic) == 2 && Count(dbirthTopic) > deviceBirthsAfterStart,
                TimeSpan.FromSeconds(8),
                "the emulator did not republish its births after a node rebirth command");

            Count(ndeathTopic).Should().Be(0, "a rebirth must not announce a node death");
            var nodeBirths = received.Where(message => message.Topic == nbirthTopic).ToList();
            BirthDeathSequence(nodeBirths[1].Payload).Should().Be(
                BirthDeathSequence(nodeBirths[0].Payload), "a rebirth stays in the same session");
            var deviceBirthsAfterNodeRebirth = Count(dbirthTopic);

            await commander.PublishAsync(
                Command($"spBv1.0/{groupId}/DCMD/{nodeId}/{deviceId}", "Device Control/Rebirth"));
            await WaitForAsync(
                () => Count(dbirthTopic) == deviceBirthsAfterNodeRebirth + 1,
                TimeSpan.FromSeconds(8),
                "the emulator did not answer a device rebirth command with a DBIRTH");

            received.Last(message => message.Topic == dbirthTopic).Payload.Metrics
                .Should().Contain(metric => metric.Name == "Speed");
            Count(nbirthTopic).Should().Be(2, "a device rebirth must not rebirth the node");
            Count(ndeathTopic).Should().Be(0);
        }
        finally
        {
            await runner.StopAsync();
            foreach (var raw in new[] { observer, commander })
            {
                if (raw.IsConnected)
                    await raw.DisconnectAsync();
            }
        }
    }

    private static ulong BirthDeathSequence(Payload payload)
    {
        var metric = payload.Metrics.Single(candidate => candidate.Name == "bdSeq");
        return metric.LongValue + metric.IntValue;
    }

    private static MqttApplicationMessage Command(string topic, string metricName)
    {
        var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        payload.Metrics.Add(new Payload.Types.Metric { Name = metricName, Datatype = 11, BooleanValue = true });
        return new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();
    }
}
