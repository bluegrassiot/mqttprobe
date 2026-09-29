using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NUnit.Framework;

namespace MqttProbe.TestInfrastructure.Fixtures;

public sealed class KeycloakFixture : IAsyncDisposable
{
    public const string Image =
        "quay.io/keycloak/keycloak@sha256:357829ec7c4693397533035092ad13b0644bcc95ded311f33a3738c4d9e9bdba"; // DevSkim: ignore DS117838 pinned image digest, not a secret

    public const string ClientId = "mqttprobe";
    public const string ClientSecret = "mqttprobe-test-secret";
    public const string AdmissionClaim = "mqttprobe_access";
    public const string AcceptedValue = "admin";
    public const string AdmittedUsername = "admitted-user";
    public const string DeniedUsername = "denied-user";
    public const string Password = "test-password";

    private const string RealmName = "mqttprobe-test";

    // Realm JSON imported at container startup. The confidential client and
    // protocol mapper are test-only configuration so the probe can authenticate
    // against Keycloak the same way it would a production IdP.
    private const string RealmJson = $$"""
        {
          "realm": "{{RealmName}}",
          "enabled": true,
          "sslRequired": "none",
          "clients": [
            {
              "clientId": "{{ClientId}}",
              "protocol": "openid-connect",
              "enabled": true,
              "publicClient": false,
              "secret": "{{ClientSecret}}",
              "clientAuthenticatorType": "client-secret",
              "standardFlowEnabled": true,
              "implicitFlowEnabled": false,
              "directAccessGrantsEnabled": false,
              "serviceAccountsEnabled": false,
              "redirectUris": [
                "https://mqttprobe.test/signin-oidc"
              ],
              "webOrigins": [
                "https://mqttprobe.test"
              ],
              "attributes": {
                "pkce.code.challenge.method": "S256",
                "post.logout.redirect.uris": "https://mqttprobe.test/signout-callback-oidc"
              },
              "protocolMappers": [
                {
                  "name": "{{AdmissionClaim}}",
                  "protocol": "openid-connect",
                  "protocolMapper": "oidc-usermodel-attribute-mapper",
                  "consentRequired": false,
                  "config": {
                    "user.attribute": "{{AdmissionClaim}}",
                    "claim.name": "{{AdmissionClaim}}",
                    "id.token.claim": "true",
                    "access.token.claim": "false",
                    "userinfo.token.claim": "false",
                    "jsonType.label": "String",
                    "multivalued": "false"
                  }
                }
              ]
            }
          ],
          "users": [
            {
              "username": "{{AdmittedUsername}}",
              "enabled": true,
              "firstName": "Admitted",
              "lastName": "User",
              "email": "admitted@mqttprobe.test",
              "emailVerified": true,
              "credentials": [
                {
                  "type": "password",
                  "value": "{{Password}}",
                  "temporary": false
                }
              ],
              "attributes": {
                "{{AdmissionClaim}}": ["{{AcceptedValue}}"]
              }
            },
            {
              "username": "{{DeniedUsername}}",
              "enabled": true,
              "firstName": "Denied",
              "lastName": "User",
              "email": "denied@mqttprobe.test",
              "emailVerified": true,
              "credentials": [
                {
                  "type": "password",
                  "value": "{{Password}}",
                  "temporary": false
                }
              ],
              "attributes": {
                "{{AdmissionClaim}}": ["viewer"]
              }
            }
          ]
        }
        """;

    private IContainer? _container;

    private KeycloakFixture()
    {
    }

    public string Authority { get; private set; } = string.Empty;

    public static async Task<KeycloakFixture> StartAsync()
    {
        var fixture = new KeycloakFixture();
        try
        {
            await fixture.SetupAsync();
            await fixture.VerifyDiscoveryAsync();
            return fixture;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            await fixture.DisposeAsync();
            Assert.Ignore("Docker is not available on this runner — integration tests skipped.");
            throw; // unreachable, keeps compiler happy
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private async Task SetupAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(8080, true)
            .WithEnvironment("KEYCLOAK_ADMIN", "admin")
            .WithEnvironment("KEYCLOAK_ADMIN_PASSWORD", "admin")
            .WithCommand("start-dev", "--import-realm")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(RealmJson),
                "/opt/keycloak/data/import/mqttprobe-test-realm.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r =>
                    r.ForPath($"/realms/{RealmName}/.well-known/openid-configuration")
                        .ForPort(8080)))
            .Build();

        await _container.StartAsync();

        var mappedPort = _container.GetMappedPublicPort(8080);
        Authority = $"http://{_container.Hostname}:{mappedPort}/realms/{RealmName}"; // DevSkim: ignore DS137138 Keycloak testcontainer serves plain HTTP in start-dev
    }

    private async Task VerifyDiscoveryAsync()
    {
        using var client = new HttpClient();
        var discoveryUrl = $"{Authority}/.well-known/openid-configuration";
        var response = await client.GetAsync(discoveryUrl);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var issuer = document.GetProperty("issuer").GetString();

        if (!string.Equals(issuer, Authority, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Discovery issuer mismatch: expected '{Authority}', got '{issuer}'.");
        }
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        if (ex is ArgumentException { ParamName: "DockerEndpointAuthConfig" })
            return true;

        var typeName = ex.GetType().Name;
        if (typeName.Contains("DockerUnavailableException"))
            return true;

        return false;
    }
}
