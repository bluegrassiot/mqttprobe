using System.Net.Http.Json;
using System.Text.Json;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Authentication;

[TestFixture]
[NonParallelizable]
public class KeycloakFixtureTests
{
    [Test]
    public async Task StartAsync_DiscoveryDocument_MatchesExpectedEndpoints()
    {
        await using var fixture = await KeycloakFixture.StartAsync();

        // Authority must be HTTP on a loopback-style host with the expected realm path.
        fixture.Authority.Should().StartWith("http://");
        fixture.Authority.Should().Contain("/realms/mqttprobe-test");
        fixture.Authority.Should().NotEndWith("/");

        // Fetch the OIDC discovery document and validate core fields.
        using var client = new HttpClient();
        var discoveryUrl = $"{fixture.Authority}/.well-known/openid-configuration";
        var response = await client.GetAsync(discoveryUrl);
        response.IsSuccessStatusCode.Should().BeTrue(
            $"discovery endpoint should return 2xx but got {response.StatusCode}");

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();

        doc.GetProperty("issuer").GetString().Should().Be(fixture.Authority);

        var authorizationEndpoint = doc.GetProperty("authorization_endpoint").GetString();
        authorizationEndpoint.Should().NotBeNullOrWhiteSpace();
        authorizationEndpoint.Should().StartWith(fixture.Authority);

        var tokenEndpoint = doc.GetProperty("token_endpoint").GetString();
        tokenEndpoint.Should().NotBeNullOrWhiteSpace();
        tokenEndpoint.Should().StartWith(fixture.Authority);

        var endSessionEndpoint = doc.GetProperty("end_session_endpoint").GetString();
        endSessionEndpoint.Should().NotBeNullOrWhiteSpace();
        endSessionEndpoint.Should().StartWith(fixture.Authority);
    }

    [Test]
    public async Task StartAsync_ExposesExpectedConstants()
    {
        await using var fixture = await KeycloakFixture.StartAsync();

        KeycloakFixture.ClientId.Should().Be("mqttprobe");
        KeycloakFixture.ClientSecret.Should().Be("mqttprobe-test-secret");
        KeycloakFixture.AdmissionClaim.Should().Be("mqttprobe_access");
        KeycloakFixture.AcceptedValue.Should().Be("admin");
        KeycloakFixture.AdmittedUsername.Should().Be("admitted-user");
        KeycloakFixture.DeniedUsername.Should().Be("denied-user");
        KeycloakFixture.Password.Should().Be("test-password");
    }
}
