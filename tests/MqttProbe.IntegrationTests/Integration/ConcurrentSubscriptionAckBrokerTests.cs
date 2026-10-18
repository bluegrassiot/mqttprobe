using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Packets;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Integration;

[TestFixture]
public class ConcurrentSubscriptionAckBrokerTests
{
    private const string DeniedTopic = "ackconcurrency/denied";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private static MtlsBrokerFixture? _broker;

    [OneTimeSetUp]
    public async Task Setup() => _broker = await MtlsBrokerFixture.StartAsync(DeniedTopic);

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (_broker is not null)
            await _broker.DisposeAsync();
    }

    [Test]
    public async Task ConcurrentSubscribeCallsKeepEveryGrantedFilterAndReportTheRejectedOne()
    {
        var granted = Enumerable.Range(0, 6)
            .Select(index => $"ackconcurrency/{Guid.NewGuid():N}/granted/{index}")
            .ToArray();

        await using var client = new MqttManagedClient();
        await client.StartAsync(BuildTlsConnectOptions("concurrent-subscribe"));

        var pending = granted
            .Select(topic => ObserveAsync(topic, client.SubscribeAsync([Filter(topic)])))
            .ToList();
        pending.Add(ObserveAsync(DeniedTopic, client.SubscribeAsync([Filter(DeniedTopic)])));

        var outcomes = await Task.WhenAll(pending);

        outcomes.Where(outcome => outcome.Topic != DeniedTopic)
            .Should().OnlyContain(outcome => outcome.Failure == null,
                "a filter the broker grants must be confirmed even while disjoint requests are in flight");
        outcomes.Single(outcome => outcome.Topic == DeniedTopic)
            .Failure.Should().BeOfType<InvalidOperationException>(
                "the rejected acknowledgement must still reach the caller that requested it");

        var snapshot = client.LiveSubscriptionSnapshot;
        snapshot.Should().NotBeNull();
        snapshot!.ConfirmedFilters.Should().BeEquivalentTo(granted);

        await client.StopAsync();
    }

    [Test]
    public async Task ConcurrentUnsubscribeAndSubscribeOverDisjointTopicsKeepBothOutcomes()
    {
        var prefix = $"ackconcurrency/{Guid.NewGuid():N}";
        var removed = $"{prefix}/removed";
        var kept = $"{prefix}/kept";
        var added = $"{prefix}/added";

        await using var client = new MqttManagedClient();
        await client.StartAsync(BuildTlsConnectOptions("concurrent-unsubscribe"));
        await client.SubscribeAsync([Filter(removed), Filter(kept)]);

        var outcomes = await Task.WhenAll(
            ObserveAsync(removed, client.UnsubscribeAsync([removed])),
            ObserveAsync(added, client.SubscribeAsync([Filter(added)])));

        outcomes.Should().OnlyContain(outcome => outcome.Failure == null);
        var snapshot = client.LiveSubscriptionSnapshot;
        snapshot.Should().NotBeNull();
        snapshot!.ConfirmedFilters.Should().BeEquivalentTo([kept, added]);

        await client.StopAsync();
    }

    private static async Task<(string Topic, Exception? Failure)> ObserveAsync(string topic, Task operation)
    {
        try
        {
            await operation.WaitAsync(_timeout);
            return (topic, null);
        }
        catch (Exception ex)
        {
            return (topic, ex);
        }
    }

    private static MqttTopicFilter Filter(string topic) =>
        new MqttTopicFilterBuilder().WithTopic(topic).Build();

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
}
