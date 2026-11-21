extern alias benchmarks;

using System.Buffers;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using benchmarks::MqttProbe.Benchmarks.V5Traffic;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class MqttV5TrafficGeneratorTests
{
    private const string PayloadJson =
        """{"temperature":22.5,"pressure":1013.25,"flowRate":12.8,"status":"active"}""";

    private static readonly string _longContentType =
        "application/vnd.mqttprobe." + new string('x', 256) + "+json";

    private static readonly V5TrafficScenarioName[] _scenarioOrder =
    [
        V5TrafficScenarioName.AllSix,
        V5TrafficScenarioName.DuplicateUserProperties,
        V5TrafficScenarioName.BinaryCorrelation,
        V5TrafficScenarioName.WhitespaceCorrelation,
        V5TrafficScenarioName.EmptyProperties,
        V5TrafficScenarioName.AbsentProperties,
        V5TrafficScenarioName.LongContentType,
        V5TrafficScenarioName.UserProperties12,
        V5TrafficScenarioName.UserProperties40,
        V5TrafficScenarioName.LongProperties,
    ];

    private static readonly byte[] _binaryCorrelation =
        [0x00, 0xFF, 0x10, 0x80, 0xC3, 0x28, 0x01, 0xFE];

    private static readonly byte[] _longCorrelation =
        Encoding.UTF8.GetBytes("long-correlation:" + new string('c', 239));

    private static readonly (string Name, string Value)[] _longProperties =
    [
        ("acceptance-long-property-name-01-" + new string('n', 48), "value-01-" + new string('v', 120)),
        ("acceptance-long-property-name-02-" + new string('m', 48), "value-02-" + new string('w', 120)),
        ("acceptance-long-property-name-03-" + new string('o', 48), "value-03-" + new string('u', 120)),
    ];

    private static readonly ExpectedScenario[] _expected =
    [
        new("all-six", "application/json", Encoding.UTF8.GetBytes("all-six-correlation"),
            "all-six/reply", MqttPayloadFormatIndicator.CharacterData, true,
            [("source", "mqttprobe"), ("suite", "v5-traffic")]),
        new("duplicate-user-properties", null, null, null, MqttPayloadFormatIndicator.Unspecified, false,
            [("trace", "first"), ("trace", "second"), ("trace", "third"),
             ("region", "eu-west"), ("region", "us-east"), ("sample", "alpha")]),
        new("binary-correlation", null, _binaryCorrelation, null, MqttPayloadFormatIndicator.Unspecified,
            false, []),
        new("whitespace-correlation", null, Encoding.UTF8.GetBytes("  alpha  beta\t\tgamma\r\ndelta  "),
            null, MqttPayloadFormatIndicator.Unspecified, false, []),
        new("empty-properties", null, [], null, MqttPayloadFormatIndicator.Unspecified, false, []),
        new("absent-properties", null, null, null, MqttPayloadFormatIndicator.Unspecified, false, []),
        new("long-content-type", _longContentType, null, null, MqttPayloadFormatIndicator.Unspecified,
            false, []),
        new("user-properties-12", null, null, null, MqttPayloadFormatIndicator.Unspecified, false,
            Metrics(12)),
        new("user-properties-40", null, null, null, MqttPayloadFormatIndicator.Unspecified, false,
            Metrics(40)),
        new("long-properties", null, _longCorrelation, null, MqttPayloadFormatIndicator.Unspecified,
            false, _longProperties),
    ];

    private MtlsBrokerFixture _broker = null!;
    private string _certificateDirectory = null!;
    private string _caCertificatePath = null!;
    private string _clientCertificatePath = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _broker = await MtlsBrokerFixture.StartAsync();
        _certificateDirectory = Path.Combine(
            Path.GetTempPath(), "mqttprobe-v5-traffic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_certificateDirectory);

        _caCertificatePath = Path.Combine(_certificateDirectory, "ca.pem");
        File.WriteAllText(_caCertificatePath, _broker.CaCert.ExportCertificatePem());

        _clientCertificatePath = Path.Combine(_certificateDirectory, "client.pfx");
        File.WriteAllBytes(_clientCertificatePath, _broker.ClientPfxBytes);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_broker is not null)
            await _broker.DisposeAsync();
        if (Directory.Exists(_certificateDirectory))
            Directory.Delete(_certificateDirectory, true);
    }

    [Test]
    public async Task AllScenarios_PublishExactV5PropertiesOverMutualTls()
    {
        var prefix = $"benchmarks/v5/integration-{Guid.NewGuid():N}";
        var collector = new ReceivedMessageCollector(_expected.Length);

        using var subscriberCertificate = X509CertificateLoader.LoadPkcs12(
            _broker.ClientPfxBytes, _broker.ClientPfxPassword, X509KeyStorageFlags.Exportable);
        using var subscriber = new MqttClientFactory().CreateMqttClient();
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            collector.Add(ReceivedMessage.From(args.ApplicationMessage));
            return Task.CompletedTask;
        };

        await subscriber.ConnectAsync(BuildSubscriberOptions(subscriberCertificate));
        try
        {
            var subscribeResult = await subscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(
                    topic: $"{prefix}/#",
                    qualityOfServiceLevel: MqttQualityOfServiceLevel.AtLeastOnce,
                    noLocal: false,
                    retainAsPublished: true,
                    retainHandling: MqttRetainHandling.SendAtSubscribe)
                .Build());

            subscribeResult.Items.Single().ResultCode.Should().Be(MqttClientSubscribeResultCode.GrantedQoS1);

            using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var exitCode = await V5TrafficRunner.RunAsync(new V5TrafficOptions
            {
                Host = "127.0.0.1",
                Port = _broker.Port,
                Prefix = prefix,
                UseTls = true,
                CaCertificatePath = _caCertificatePath,
                ClientCertificatePath = _clientCertificatePath,
                ClientCertificatePassword = _broker.ClientPfxPassword,
                Scenarios = _scenarioOrder,
                Repeat = 1,
                Interval = TimeSpan.Zero,
            }, runCts.Token);

            exitCode.Should().Be(0);

            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var messages = await collector.WaitAsync(TimeSpan.FromSeconds(15), waitCts.Token);
            AssertMessages(messages, prefix);
            await AssertNoRetainedHistoryAsync(prefix, subscriberCertificate);
        }
        finally
        {
            if (subscriber.IsConnected)
                await subscriber.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
        }
    }

    private async Task AssertNoRetainedHistoryAsync(string prefix, X509Certificate2 certificate)
    {
        using var retainedSubscriber = new MqttClientFactory().CreateMqttClient();
        var retainedMessage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        retainedSubscriber.ApplicationMessageReceivedAsync += _ =>
        {
            retainedMessage.TrySetResult(true);
            return Task.CompletedTask;
        };

        await retainedSubscriber.ConnectAsync(BuildSubscriberOptions(certificate));
        try
        {
            await retainedSubscriber.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(
                    topic: $"{prefix}/#",
                    qualityOfServiceLevel: MqttQualityOfServiceLevel.AtLeastOnce,
                    noLocal: false,
                    retainAsPublished: true,
                    retainHandling: MqttRetainHandling.SendAtSubscribe)
                .Build());

            var completed = await Task.WhenAny(
                retainedMessage.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            completed.Should().NotBe(
                retainedMessage.Task,
                "a fresh subscription must not receive retained history from this run");
        }
        finally
        {
            if (retainedSubscriber.IsConnected)
                await retainedSubscriber.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
        }
    }

    private MqttClientOptions BuildSubscriberOptions(X509Certificate2 certificate) =>
        new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker.Port)
            .WithClientId($"mqttprobe-v5-traffic-test-{Guid.NewGuid():N}")
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(true)
            .WithTlsOptions(tls => tls
                .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13) // DevSkim: ignore DS440020,DS112836,DS440001
                .WithClientCertificates(new X509Certificate2Collection(certificate))
                .WithTrustChain(new X509Certificate2Collection(_broker.CaCert)))
            .Build();

    private static void AssertMessages(IReadOnlyList<ReceivedMessage> messages, string prefix)
    {
        messages.Should().HaveCount(_expected.Length);
        messages.Select(message => message.Topic)
            .Should().Equal(_expected.Select(scenario => $"{prefix}/{scenario.Name}"));

        var payload = Encoding.UTF8.GetBytes(PayloadJson);
        for (var i = 0; i < messages.Count; i++)
        {
            var actual = messages[i];
            var expected = _expected[i];
            var because = $"scenario '{expected.Name}'";

            actual.Payload.Should().Equal(payload, because);
            actual.Retain.Should().BeFalse($"{because} must publish with retain disabled");
            actual.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtLeastOnce, because);
            actual.ContentType.Should().Be(expected.ContentType, because);

            if (expected.CorrelationData is null)
                actual.CorrelationData.Should().BeNull(because);
            else
                actual.CorrelationData.Should().Equal(expected.CorrelationData, because);

            actual.ResponseTopic.Should().Be(
                expected.ResponseTopicSuffix is null
                    ? null
                    : $"{prefix}/{expected.ResponseTopicSuffix}",
                because);

            actual.PayloadFormatIndicator.Should().Be(expected.PayloadFormatIndicator, because);

            if (expected.HasExpiry)
                actual.MessageExpiryInterval.Should().BeInRange(1u, 60u, $"{because} may be decremented by the broker");
            else
                actual.MessageExpiryInterval.Should().Be(0u, because);

            actual.UserProperties.Should().Equal(expected.UserProperties, because);
        }
    }

    private static (string Name, string Value)[] Metrics(int count) =>
        Enumerable.Range(1, count)
            .Select(index => ($"metric-{index:00}", $"value-{index:00}"))
            .ToArray();

    private sealed record ExpectedScenario(
        string Name,
        string? ContentType,
        byte[]? CorrelationData,
        string? ResponseTopicSuffix,
        MqttPayloadFormatIndicator PayloadFormatIndicator,
        bool HasExpiry,
        (string Name, string Value)[] UserProperties);

    private sealed record ReceivedMessage(
        string Topic,
        byte[] Payload,
        bool Retain,
        MqttQualityOfServiceLevel QualityOfServiceLevel,
        string? ContentType,
        byte[]? CorrelationData,
        string? ResponseTopic,
        MqttPayloadFormatIndicator PayloadFormatIndicator,
        uint MessageExpiryInterval,
        (string Name, string Value)[] UserProperties)
    {
        public static ReceivedMessage From(MqttApplicationMessage message) =>
            new(
                message.Topic,
                message.Payload.ToArray(),
                message.Retain,
                message.QualityOfServiceLevel,
                message.ContentType,
                message.CorrelationData?.ToArray(),
                message.ResponseTopic,
                message.PayloadFormatIndicator,
                message.MessageExpiryInterval,
                message.UserProperties?
                    .Select(property => (property.Name, property.ReadValueAsString()))
                    .ToArray() ?? []);
    }

    private sealed class ReceivedMessageCollector(int expected)
    {
        private readonly List<ReceivedMessage> _messages = [];
        private readonly TaskCompletionSource _expectedReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(ReceivedMessage message)
        {
            lock (_messages)
            {
                _messages.Add(message);
                if (_messages.Count >= expected)
                    _expectedReached.TrySetResult();
            }
        }

        public async Task<IReadOnlyList<ReceivedMessage>> WaitAsync(
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                await _expectedReached.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);

            lock (_messages)
                return _messages.ToArray();
        }
    }
}
