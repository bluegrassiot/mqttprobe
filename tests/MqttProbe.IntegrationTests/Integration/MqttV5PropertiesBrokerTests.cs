using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Formatter;
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
using MqttProbe.TestInfrastructure;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class MqttV5PropertiesBrokerTests
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
    public async Task RetainedV5Message_PropertiesSurviveBrokerRoundTrip_AgainstBroker()
    {
        var topic = $"v5/props/{Guid.NewGuid():N}";
        const string payload = """{"device":"integ","temperature":21.5}""";

        await PublishRetainedWithPropertiesAsync(topic, payload);

        var message = await ReadSingleMessageAsync(topic);

        message.Payload.Should().Be(payload);
        message.RetainedMessage.Should().BeTrue();
        message.ContentType.Should().Be("application/json");
        message.ResponseTopic.Should().Be($"{topic}/reply");
        message.CorrelationData.Should().Equal("corr-1"u8.ToArray());
        message.MessageExpiryInterval.Should().NotBeNull().And.BeInRange(1u, 3600u,
            "the broker decrements the expiry by the time the message sat retained");
        message.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.CharacterData);
        message.UserProperties.Should().Equal(
            new MqttMessageUserProperty("source", "integration-test"),
            new MqttMessageUserProperty("site", "lab"),
            new MqttMessageUserProperty("schema", "v1"));
    }

    [Test]
    public async Task OutboundMapperThroughManagedClient_PropertiesSurviveBrokerRoundTrip_AgainstBroker()
    {
        var topic = $"v5/outbound/{Guid.NewGuid():N}";
        const string payload = """{"device":"publisher","temperature":22.5}""";

        using var publisher = new MqttManagedClient();
        await publisher.StartAsync(BuildConnectOptions("v5-outbound-publisher", MqttProtocolVersion.V500));
        await WaitUntilAsync(() => publisher.IsConnected);

        await publisher.EnqueueAsync(OutboundMessageMapper.Build(
            new MqttMessage(payload, topic, true, MqttQualityOfServiceLevel.AtLeastOnce)
            {
                ContentType = "application/json",
                ResponseTopic = $"{topic}/reply",
                CorrelationData = "order  42"u8.ToArray(),
                MessageExpiryInterval = 3600,
                PayloadFormatIndicator = MqttPayloadFormatIndicator.CharacterData,
                UserProperties =
                [
                    new MqttMessageUserProperty("trace", "1"),
                    new MqttMessageUserProperty("trace", "2"),
                    new MqttMessageUserProperty("site", " lab ")
                ]
            }));
        await publisher.StopAsync();

        var message = await ReadSingleMessageAsync(topic);

        message.Payload.Should().Be(payload);
        message.RetainedMessage.Should().BeTrue();
        message.ContentType.Should().Be("application/json");
        message.ResponseTopic.Should().Be($"{topic}/reply");
        message.CorrelationData.Should().Equal("order  42"u8.ToArray());
        message.MessageExpiryInterval.Should().NotBeNull().And.BeInRange(1u, 3600u);
        message.PayloadFormatIndicator.Should().Be(MqttPayloadFormatIndicator.CharacterData);
        message.UserProperties.Should().Equal(
            new MqttMessageUserProperty("trace", "1"),
            new MqttMessageUserProperty("trace", "2"),
            new MqttMessageUserProperty("site", " lab "));
    }

    [Test]
    public async Task EnqueueAsync_OnV311Session_RejectsV5Properties_AgainstBroker()
    {
        using var publisher = new MqttManagedClient();
        await publisher.StartAsync(BuildConnectOptions("v311-publisher", MqttProtocolVersion.V311));
        await WaitUntilAsync(() => publisher.IsConnected);

        var message = OutboundMessageMapper.Build(
            new MqttMessage("p", $"v5/reject/{Guid.NewGuid():N}", false, MqttQualityOfServiceLevel.AtMostOnce)
            {
                UserProperties = [new MqttMessageUserProperty("k", "v")]
            });

        var act = () => publisher.EnqueueAsync(message);

        await act.Should().ThrowAsync<NotSupportedException>();
        await publisher.StopAsync();
    }

    private static async Task<MqttMessage> ReadSingleMessageAsync(string topic)
    {
        using var client = new MqttManagedClient();

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
            Name = "V5PropertiesTest",
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
            client, Substitute.For<ILogger<SubscriptionManager>>(), new NoOpUserNotifier(),
            Substitute.For<IConnectionSettings>(), uiSettings, sessionState);

        using var storeManager = new MessageStoreManager(
            client, Substitute.For<ILogger<MessageStoreManager>>(), perfSettings,
            Substitute.For<IUxMetricsService>(), BuildBuiltInPipeline(), sparkplug);

        await storeManager.Start();
        await client.StartAsync(BuildConnectOptions("v5-props-subscriber", MqttProtocolVersion.V500));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && storeManager.TotalStoredMessages == 0)
            await Task.Delay(200);

        var messages = await MessageStoreReads.ReadMessagesAsync(storeManager, topic, 10);
        var message = messages.Should().ContainSingle().Subject;

        await storeManager.Stop();
        await client.StopAsync();
        return message;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        condition().Should().BeTrue("the condition should be met within the deadline");
    }

    private static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static async Task PublishRetainedWithPropertiesAsync(string topic, string payload)
    {
        using var publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(BuildConnectOptions("v5-props-publisher", MqttProtocolVersion.V500).ClientOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .WithContentType("application/json")
            .WithResponseTopic($"{topic}/reply")
            .WithCorrelationData("corr-1"u8.ToArray())
            .WithMessageExpiryInterval(3600)
            .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.CharacterData)
            .WithUserProperty("source", Utf8("integration-test"))
            .WithUserProperty("site", Utf8("lab"))
            .WithUserProperty("schema", Utf8("v1"))
            .Build();

        await publisher.PublishAsync(message);
        await publisher.DisconnectAsync(new MqttClientDisconnectOptions());
    }

    private static MqttManagedClientOptions BuildConnectOptions(string clientIdSuffix, MqttProtocolVersion version)
    {
        var clientOpts = new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker!.Port)
            .WithClientId($"integ-{clientIdSuffix}-{Guid.NewGuid():N}")
            .WithProtocolVersion(version)
            .WithCleanStart()
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
