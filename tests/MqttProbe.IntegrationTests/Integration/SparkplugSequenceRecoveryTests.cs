using System.Security.Cryptography.X509Certificates;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.TestInfrastructure.Fixtures;
using Org.Eclipse.Tahu.Protobuf;

namespace MqttProbe.IntegrationTests.Integration;

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

[TestFixture]
public sealed class SparkplugSequenceRecoveryTests
{
    private static MtlsBrokerFixture? _broker;

    [OneTimeSetUp]
    public async Task Setup() => _broker = await MtlsBrokerFixture.StartAsync();

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_broker is not null)
            await _broker.DisposeAsync();
    }

    [Test]
    public async Task ExpiredGapWithoutMoreInbound_PublishesNcmdToBroker()
    {
        await using var context = await SequenceContext.CreateAsync(_broker!, "timeout");
        context.Client.IsConnected.Should().BeTrue();
        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);
        await context.PublishDataAsync(3);
        await context.WaitForStateAsync(SpbSequenceState.GapDetected);
        var commandTopic = await context.NodeRebirthReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));

        commandTopic.Should().Be($"spBv1.0/{context.GroupId}/NCMD/{context.NodeId}");
    }

    [Test]
    public async Task LateMissingSequenceBeforeDeadline_DoesNotPublishNcmd()
    {
        await using var context = await SequenceContext.CreateAsync(_broker!, "late");
        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);
        await context.PublishDataAsync(3);
        await context.PublishDataAsync(2);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);

        await Task.Delay(600);

        context.NodeRebirthReceived.Task.IsCompleted.Should().BeFalse();
    }

    [Test]
    public async Task GapFollowedByDisconnect_DoesNotQueueNcmdForReconnect()
    {
        await using var context = await SequenceContext.CreateAsync(_broker!, "disconnect");
        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);
        await context.PublishDataAsync(3);
        await context.WaitForStateAsync(SpbSequenceState.GapDetected);
        await context.Client.StopAsync();
        await Task.Delay(400);
        var commandsBeforeReconnect = context.NodeRebirthCount;
        await context.Client.StartAsync(BuildTlsConnectOptions("sequence-disconnect-reconnect"));
        await Task.Delay(600);

        context.NodeRebirthCount.Should().Be(commandsBeforeReconnect);
    }

    [TestCase("specific-device")]
    public async Task DeviceSubscriptionCoverage_ExactDeviceDoesNotQualifyForSequenceRecovery(string deviceFilter)
    {
        await using var context = await SequenceContext.CreateAsync(_broker!, "exact", deviceFilter);
        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);
        await context.PublishDataAsync(3);
        await context.WaitForStateAsync(SpbSequenceState.GapDetected);
        await Task.Delay(600);

        context.NodeRebirthCount.Should().Be(0);
    }

    [TestCase("spBv1.0/#")]
    [TestCase("spBv1.0/{groupId}/#")]
    [TestCase("spBv1.0/{groupId}/{verb}/{nodeId}/#")]
    public async Task DeviceSubscriptionCoverage_BroadTerminalWildcardQualifiesForRecovery(string deviceFilter)
    {
        await using var context = await SequenceContext.CreateAsync(_broker!, "broad", deviceFilter);
        await context.PublishBirthAsync(1);
        await context.WaitForStateAsync(SpbSequenceState.InOrder);
        await context.PublishDataAsync(3);
        await context.WaitForStateAsync(SpbSequenceState.GapDetected);

        (await context.NodeRebirthReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Be($"spBv1.0/{context.GroupId}/NCMD/{context.NodeId}");
    }

    private static MqttManagedClientOptions BuildTlsConnectOptions(string clientIdSuffix)
    {
        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker!.Port)
            .WithClientId($"sequence-{clientIdSuffix}-{Guid.NewGuid():N}")
            .WithCleanSession()
            .WithTlsOptions(options => options
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13) // DevSkim: ignore DS440020,DS112836,DS440001
                .WithClientCertificates(new X509Certificate2Collection(
                    X509CertificateLoader.LoadPkcs12(
                        _broker.ClientPfxBytes,
                        _broker.ClientPfxPassword,
                        X509KeyStorageFlags.Exportable)))
                .WithCertificateValidationHandler(context =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.ExtraStore.Add(_broker.CaCert);
                    chain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority
                        | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                        | X509VerificationFlags.IgnoreEndRevocationUnknown
                        | X509VerificationFlags.IgnoreRootRevocationUnknown;
                    return chain.Build((X509Certificate2)context.Certificate);
                }))
            .Build();

        return new MqttManagedClientOptions
        {
            ClientOptions = clientOptions,
            AutoReconnectDelay = TimeSpan.FromSeconds(60)
        };
    }

    private sealed class SequenceContext : IAsyncDisposable
    {
        private readonly IMqttClient _publisher;
        private readonly IMqttClient _observer;
        private readonly MessageStoreManager _store;
        private readonly Func<int> _getNodeRebirthCount;

        private SequenceContext(
            string groupId,
            string nodeId,
            MqttManagedClient client,
            IMqttClient publisher,
            IMqttClient observer,
            MessageStoreManager store,
            ISparkplugTopologyService topology,
            ISparkplugCommandService commands,
            TaskCompletionSource<string> nodeRebirthReceived,
            Func<int> getNodeRebirthCount)
        {
            GroupId = groupId;
            NodeId = nodeId;
            Client = client;
            _publisher = publisher;
            _observer = observer;
            _store = store;
            Topology = topology;
            Commands = commands;
            NodeRebirthReceived = nodeRebirthReceived;
            _getNodeRebirthCount = getNodeRebirthCount;
        }

        public string GroupId { get; }
        public string NodeId { get; }
        public MqttManagedClient Client { get; }
        public TaskCompletionSource<string> NodeRebirthReceived { get; }
        public int NodeRebirthCount => _getNodeRebirthCount();
        public ISparkplugTopologyService Topology { get; }
        public ISparkplugCommandService Commands { get; }

        public static async Task<SequenceContext> CreateAsync(
            MtlsBrokerFixture broker, string suffix, string deviceFilter = "+")
        {
            var groupId = $"seq-{suffix}-{Guid.NewGuid():N}";
            const string nodeId = "node";
            var deviceTopics = new[] { "DBIRTH", "DDATA", "DDEATH" }
                .Select(verb => deviceFilter.StartsWith("spBv1.0/", StringComparison.Ordinal)
                    ? deviceFilter.Replace("{groupId}", groupId, StringComparison.Ordinal)
                        .Replace("{verb}", verb, StringComparison.Ordinal)
                        .Replace("{nodeId}", nodeId, StringComparison.Ordinal)
                    : $"spBv1.0/{groupId}/{verb}/{nodeId}/{deviceFilter}")
                .ToArray();
            var client = new MqttManagedClient();
            var publisher = new MqttClientFactory().CreateMqttClient();
            var observer = new MqttClientFactory().CreateMqttClient();
            var commandReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commandCount = 0;
            var observerOptions = BuildTlsConnectOptions($"sequence-observer-{suffix}");
            await observer.ConnectAsync(observerOptions.ClientOptions);
            var subscriptionResult = await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic($"spBv1.0/{groupId}/NCMD/{nodeId}").Build())
                .Build());
            subscriptionResult.Items.Should().ContainSingle();
            observer.ApplicationMessageReceivedAsync += args =>
            {
                Interlocked.Increment(ref commandCount);
                commandReceived.TrySetResult(args.ApplicationMessage.Topic);
                return Task.CompletedTask;
            };
            await publisher.ConnectAsync(BuildTlsConnectOptions($"sequence-publisher-{suffix}").ClientOptions);

            var sparkplug = Substitute.For<ISparkplugSettings>();
            sparkplug.Sparkplug.Returns(new SparkplugSettings
            {
                AutoRequestRebirth = true,
                RebirthCooldownSeconds = 5,
                ReorderTimeoutMilliseconds = 250
            });
            var topology = new SparkplugTopologyService(sparkplugSettings: sparkplug);
            var commands = new SparkplugCommandService(
                client,
                Substitute.For<ILogger<SparkplugCommandService>>(),
                sparkplug,
                topology);
            var subscriptions = Substitute.For<ISubscriptionManager>();
            subscriptions.Subscriptions.Returns(new[]
            {
                new SubscribedTopic { Topic = $"spBv1.0/{groupId}/NBIRTH/{nodeId}" },
                new SubscribedTopic { Topic = $"spBv1.0/{groupId}/NDATA/{nodeId}" },
                new SubscribedTopic { Topic = deviceTopics[0] },
                new SubscribedTopic { Topic = deviceTopics[1] },
                new SubscribedTopic { Topic = deviceTopics[2] }
            });
            var performance = Substitute.For<IPerformanceSettings>();
            performance.Performance.Returns(new PerformanceSettings { MaxStoredMessages = 100, MaxMessagesPerSecond = 50000 });
            var store = new MessageStoreManager(
                client,
                Substitute.For<ILogger<MessageStoreManager>>(),
                performance,
                Substitute.For<IUxMetricsService>(),
                BuildPipeline(),
                sparkplug,
                topology,
                commands,
                subscriptionManager: subscriptions);
            await store.Start();
            var nodeFilters = new[]
            {
                new MqttTopicFilterBuilder().WithTopic($"spBv1.0/{groupId}/NBIRTH/{nodeId}")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
                new MqttTopicFilterBuilder().WithTopic($"spBv1.0/{groupId}/NDATA/{nodeId}")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            }.Concat(deviceTopics.Select(topic => new MqttTopicFilterBuilder().WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build())).ToArray();
            await client.SubscribeAsync(nodeFilters);
            await client.StartAsync(BuildTlsConnectOptions($"sequence-client-{suffix}"));
            await client.SubscribeAsync(nodeFilters);

            return new SequenceContext(
                groupId, nodeId, client, publisher, observer, store, topology, commands, commandReceived,
                () => Volatile.Read(ref commandCount));
        }

        public Task PublishBirthAsync(ulong sequence)
        {
            var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Seq = sequence };
            payload.Metrics.Add(new Payload.Types.Metric { Name = "bdSeq", Datatype = 5, IntValue = 1 });
            return PublishAsync($"spBv1.0/{GroupId}/NBIRTH/{NodeId}", payload);
        }

        public Task PublishDataAsync(ulong sequence)
        {
            var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Seq = sequence };
            payload.Metrics.Add(new Payload.Types.Metric { Name = "temperature", Datatype = 10, DoubleValue = 21.5 });
            return PublishAsync($"spBv1.0/{GroupId}/NDATA/{NodeId}", payload);
        }

        public async Task WaitForStateAsync(SpbSequenceState state)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline
                   && (!Topology.Groups.TryGetValue(GroupId, out var group)
                       || !group.Nodes.TryGetValue(NodeId, out var node)
                       || node.Sequence.State != state))
                await Task.Delay(25);

            Topology.Groups[GroupId].Nodes[NodeId].Sequence.State.Should().Be(state);
        }

        private Task PublishAsync(string topic, Payload payload) => _publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload.ToByteArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        private static PayloadPipeline BuildPipeline()
        {
            var builder = new PluginRegistryBuilder();
            BuiltInPluginRegistration.RegisterBuiltIns(builder);
            return new PayloadPipeline(builder.Build(), Substitute.For<ILogger<PayloadPipeline>>());
        }

        public async ValueTask DisposeAsync()
        {
            await _store.Stop();
            await Client.StopAsync();
            await _publisher.DisconnectAsync();
            await _observer.DisconnectAsync();
            _publisher.Dispose();
            _observer.Dispose();
            _store.Dispose();
            await Client.DisposeAsync();
        }
    }
}
