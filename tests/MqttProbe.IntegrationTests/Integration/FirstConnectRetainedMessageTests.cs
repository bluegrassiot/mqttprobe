using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MqttProbe.Core;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Plugins.BuiltIn;
using MqttProbe.Core.Services.Plugins.Pipeline;
using MqttProbe.Core.Services.Plugins.Registry;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class FirstConnectRetainedMessageTests
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
    public async Task Start_BeforeConnect_RetainedMessage_IsCaptured_AgainstBroker()
    {
        // Unique topic avoids cross-test retained pollution.
        var topic = $"race/retained/{Guid.NewGuid():N}";
        const string payload = "hello-retained-integration";

        await PublishRetainedAsync(topic, payload);

        using var client = new MqttManagedClient();

        var subLogger = Substitute.For<ILogger<SubscriptionManager>>();
        var storeLogger = Substitute.For<ILogger<MessageStoreManager>>();
        var notifier = new NoOpUserNotifier();

        var perfConfig = new AppConfiguration
        {
            Performance = new PerformanceSettings { MaxStoredMessages = 100, MaxMessagesPerSecond = 50_000 }
        };
        var perfSettings = Substitute.For<IPerformanceSettings>();
        perfSettings.Performance.Returns(perfConfig.Performance);

        var sparkplug = Substitute.For<ISparkplugSettings>();
        sparkplug.Sparkplug.Returns(new SparkplugSettings { EnrichAliasNames = true });

        var connection = new Connection
        {
            Name = "RetainedTest",
            Host = "127.0.0.1",
            Port = _broker!.Port,
            UseTls = true,
            AllowUntrustedCertificate = true,
            SubscribedTopics =
            [
                new SubscribedTopic
                {
                    Topic = topic,
                    QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
                }
            ]
        };

        var uiSettings = Substitute.For<IUiSettings>();
        uiSettings.Ui.Returns(new UiPreferences { AutoResubscribe = true });

        var sessionState = Substitute.For<ISessionState>();
        sessionState.SelectedConnection.Returns(connection);

        using var subscriptionManager = new SubscriptionManager(
            client, subLogger, notifier,
            Substitute.For<IConnectionSettings>(), uiSettings, sessionState);

        var pipeline = BuildBuiltInPipeline();
        using var storeManager = new MessageStoreManager(
            client, storeLogger, perfSettings,
            Substitute.For<IUxMetricsService>(), pipeline, sparkplug);

        await storeManager.Start();
        storeManager.IsListening.Should().BeTrue();

        var connectOptions = BuildTlsConnectOptions("retained-test");
        await client.StartAsync(connectOptions);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (storeManager.TotalStoredMessages > 0)
                break;
            await Task.Delay(200);
        }

        storeManager.TotalStoredMessages.Should().BeGreaterThanOrEqualTo(1,
            "retained message should have been captured when Start() ran before connect");

        storeManager.MessageStores.Should().ContainKey("race",
            "topic tree should contain the 'race' top-level node");

        var raceStore = storeManager.MessageStores["race"];
        raceStore.SubTopics.Should().NotBeNull();
        raceStore.SubTopics.Should().ContainKey("retained",
            "sub-topic 'retained' should exist under 'race'");

        var retainedTopicNode = raceStore.SubTopics!["retained"];
        retainedTopicNode.SubTopics.Should().NotBeNull();
        retainedTopicNode.SubTopics.Should().NotBeEmpty(
            "the Guid segment should appear as a sub-topic");

        var guidNode = retainedTopicNode.SubTopics!.Values.First();
        guidNode.Messages.Should().NotBeNull();
        guidNode.Messages.Should().ContainSingle(
            "exactly one retained message should be present");

        var message = guidNode.Messages!.First();
        message.Payload.Should().Be(payload);
        message.RetainedMessage.Should().BeTrue(
            "the broker flagged the message as retained");

        await storeManager.Stop();
        await client.StopAsync();
    }

    private static async Task PublishRetainedAsync(string topic, string payload)
    {
        using var publisher = new MqttClientFactory().CreateMqttClient();
        var options = BuildTlsConnectOptions("retained-publisher");
        await publisher.ConnectAsync(options.ClientOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build();

        await publisher.PublishAsync(message);
        await publisher.DisconnectAsync(new MqttClientDisconnectOptions());
    }

    private static MqttManagedClientOptions BuildTlsConnectOptions(string clientIdSuffix)
    {
        var clientOpts = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker!.Port)
            .WithClientId($"integ-{clientIdSuffix}-{Guid.NewGuid():N}")
            .WithCleanSession()
            .WithTlsOptions(o => o
                .WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13) // DevSkim: ignore DS440020,DS112836,DS440001
                .WithClientCertificates(new X509Certificate2Collection(
                    X509CertificateLoader.LoadPkcs12(
                        _broker.ClientPfxBytes, _broker.ClientPfxPassword,
                        X509KeyStorageFlags.Exportable)))
                .WithCertificateValidationHandler(ctx =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.ExtraStore.Add(_broker.CaCert);
                    chain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority
                        | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                        | X509VerificationFlags.IgnoreEndRevocationUnknown
                        | X509VerificationFlags.IgnoreRootRevocationUnknown;
                    return chain.Build((X509Certificate2)ctx.Certificate);
                }))
            .Build();

        return new MqttManagedClientOptions
        {
            ClientOptions = clientOpts,
            AutoReconnectDelay = TimeSpan.FromSeconds(60)
        };
    }

    private static PayloadPipeline BuildBuiltInPipeline()
    {
        var builder = new PluginRegistryBuilder();
        BuiltInPluginRegistration.RegisterBuiltIns(builder);
        var registry = builder.Build();
        return new PayloadPipeline(registry, Substitute.For<ILogger<PayloadPipeline>>());
    }

    private sealed class NoOpUserNotifier : IUserNotifier
    {
        public void Notify(UserNotification notification) { }
    }
}
