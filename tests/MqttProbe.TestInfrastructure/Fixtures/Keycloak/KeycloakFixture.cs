using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public const string AdminUsername = "admin";
    public const string AdminPassword = "admin";

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
            .WithExtraHost("host.docker.internal", "host-gateway")
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

    // The URL carries an ephemeral port, so it cannot live in the imported realm JSON.
    public async Task SetBackchannelLogoutUrlAsync(
        string backchannelLogoutUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backchannelLogoutUrl);

        var clientJson = await GetClientRepresentationAsync(cancellationToken);
        var client = JsonNode.Parse(clientJson)?.AsObject()
            ?? throw new InvalidOperationException("Keycloak returned an unparsable client representation.");

        var attributes = client["attributes"] as JsonObject ?? new JsonObject();
        attributes["backchannel.logout.url"] = backchannelLogoutUrl;
        attributes["backchannel.logout.session.required"] = "true";
        client["attributes"] = attributes;

        var clientId = client["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Keycloak client representation has no id.");

        await SendAdminRequestAsync(
            HttpMethod.Put,
            $"/admin/realms/{RealmName}/clients/{clientId}",
            new StringContent(client.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);

        var stored = await GetBackchannelLogoutUrlAsync(cancellationToken);
        if (!string.Equals(stored, backchannelLogoutUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Keycloak stored backchannel.logout.url '{stored ?? "<null>"}' instead of " +
                $"'{backchannelLogoutUrl}', so provider back-channel logout cannot reach the test.");
        }
    }

    public async Task<string?> GetBackchannelLogoutUrlAsync(CancellationToken cancellationToken = default)
    {
        var clientJson = await GetClientRepresentationAsync(cancellationToken);
        var client = JsonNode.Parse(clientJson)?.AsObject();
        return client?["attributes"]?["backchannel.logout.url"]?.GetValue<string>();
    }

    // Distinguishes "Keycloak never sent it" from "Keycloak could not reach it".
    public async Task<string> ProbeHostTcpAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        if (_container is null)
            return "container not running";

        try
        {
            var script = $"exec 3<>/dev/tcp/{host}/{port} && echo reachable";
            var result = await _container.ExecAsync(["/bin/bash", "-c", script], cancellationToken);
            return $"exit {result.ExitCode}: {result.Stdout.Trim()} {result.Stderr.Trim()}".Trim();
        }
        catch (Exception ex)
        {
            return $"exec failed: {ex.GetType().Name}";
        }
    }

    // stdout then stderr: Keycloak writes its own lines to stdout, so they must
    // come last or a log tail would only ever show JVM stderr.
    public async Task<string> GetContainerLogsAsync(CancellationToken cancellationToken = default)
    {
        if (_container is null)
            return string.Empty;

        var (stdout, stderr) = await _container.GetLogsAsync(ct: cancellationToken);
        return string.Concat(stderr, stdout);
    }

    private string BaseUrl =>
        Authority[..Authority.IndexOf("/realms/", StringComparison.Ordinal)];

    private async Task<string> GetClientRepresentationAsync(CancellationToken cancellationToken)
    {
        var listJson = await SendAdminRequestAsync(
            HttpMethod.Get,
            $"/admin/realms/{RealmName}/clients?clientId={ClientId}",
            content: null,
            cancellationToken);

        using var list = JsonDocument.Parse(listJson);
        var match = list.RootElement.EnumerateArray()
            .FirstOrDefault(e => e.TryGetProperty("clientId", out var id) &&
                                 id.GetString() == ClientId);

        if (match.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Keycloak has no client '{ClientId}' in realm '{RealmName}'.");

        var clientResourceId = match.GetProperty("id").GetString()!;
        return await SendAdminRequestAsync(
            HttpMethod.Get,
            $"/admin/realms/{RealmName}/clients/{clientResourceId}",
            content: null,
            cancellationToken);
    }

    private async Task<string> SendAdminRequestAsync(
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var token = await GetAdminAccessTokenAsync(cancellationToken);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(method, $"{BaseUrl}{pathAndQuery}");
        request.Content = content;
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string> GetAdminAccessTokenAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUsername,
            ["password"] = AdminPassword
        });

        using var response = await client.PostAsync(
            $"{BaseUrl}/realms/master/protocol/openid-connect/token", content, cancellationToken);

        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Keycloak admin token response has no access_token.");
    }
}
