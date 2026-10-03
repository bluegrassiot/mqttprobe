using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MqttProbe.TestInfrastructure.Fixtures;

namespace MqttProbe.IntegrationTests.Authentication;

[TestFixture]
[NonParallelizable]
public class KeycloakBackchannelLogoutTests : IAsyncDisposable
{
    private static readonly TimeSpan _providerDeliveryTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex _jwtPattern = new(
        "[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}", RegexOptions.Compiled);

    private KeycloakFixture? _fixture;

    [OneTimeSetUp]
    public async Task StartKeycloak()
    {
        _fixture = await KeycloakFixture.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_fixture is not null)
            await _fixture.DisposeAsync();
    }

    // The user logs out at the IdP itself; the logout token reaches the app over
    // the bridge's real TCP socket. Nothing here crafts that POST.
    [Test]
    public async Task ProviderInitiatedLogout_PostsSignedLogoutTokenAndRevokesMatchedSession()
    {
        var (factory, appClient, driver) = CreateAppClientAndDriver();
        await using var bridge = await BackchannelLogoutBridge.StartAsync();

        await using (factory)
        using (appClient)
        using (driver)
        {
            bridge.ForwardToApp(factory.Server.CreateHandler());
            await _fixture!.SetBackchannelLogoutUrlAsync(bridge.BackchannelLogoutUrl);

            await AuthenticateAdmittedAsync(appClient, driver);

            await LogoutAtProviderAsync(driver);
            var captured = await WaitForProviderDeliveryAsync(bridge);

            await AssertDeliveryAsync(captured);

            using (var afterLogout = await appClient.GetAsync("/"))
            {
                afterLogout.StatusCode.Should().Be(
                    HttpStatusCode.Redirect,
                    "the app must reject the cookie once the matched session is revoked");

                var loginUri = afterLogout.Headers.Location;
                loginUri.Should().NotBeNull();
                ResolveLocation(loginUri, appClient.BaseAddress!).AbsolutePath.Should().Be(
                    "/Login", "the revoked session must force a fresh login");
            }
        }
    }

    private async Task LogoutAtProviderAsync(OidcProtocolDriver driver)
    {
        var endSession = new Uri($"{_fixture!.Authority}/protocol/openid-connect/logout");

        // Some Keycloak pages carry no form; the delivery wait decides the outcome.
        try
        {
            using var response = await driver.SubmitProviderConfirmationAsync(endSession);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task<CapturedBackchannelLogoutRequest> WaitForProviderDeliveryAsync(
        BackchannelLogoutBridge bridge)
    {
        var captured = await bridge.TryWaitForRequestAsync(_providerDeliveryTimeout);
        if (captured is not null)
            return captured;

        var storedUrl = await _fixture!.GetBackchannelLogoutUrlAsync();
        var reachable = await _fixture.ProbeHostTcpAsync(bridge.ContainerHost, bridge.Port);
        var logs = await _fixture.GetContainerLogsAsync();

        Assert.Fail(
            $"No provider-initiated POST reached {bridge.BackchannelLogoutUrl} after the user " +
            "logged out at the provider. client backchannel.logout.url is " +
            $"'{storedUrl ?? "<null>"}'; container TCP probe to " +
            $"{bridge.ContainerHost}:{bridge.Port} => {reachable}." +
            "\n--- Keycloak log lines mentioning logout/backchannel/errors ---\n" +
            Sanitize(SummarizeLogs(logs)));

        throw new InvalidOperationException("unreachable");
    }

    private static string SummarizeLogs(string logs)
    {
        var lines = logs
            .Split('\n')
            .Where(line =>
                line.Contains("logout", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("backchannel", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            .TakeLast(30);

        return string.Join('\n', lines);
    }

    private async Task AssertDeliveryAsync(CapturedBackchannelLogoutRequest captured)
    {
        captured.Method.Should().Be("POST", "the provider must call the back-channel logout route");
        captured.PathAndQuery.Should().Be("/oidc/backchannel-logout");
        captured.ContentType.Should().StartWith(
            "application/x-www-form-urlencoded",
            "back-channel logout is delivered as a logout_token form post");

        // Docker Desktop relays container traffic through a local proxy, so the peer
        // is loopback here; provider origin is proven by the signature check below.
        captured.RemoteIpAddress.Should().NotBeNull(
            "the bridge must record the peer address of the caller");

        var form = HttpUtility.ParseQueryString(captured.Body);
        var logoutToken = form.Get("logout_token");
        AssertLogoutTokenClaims(logoutToken);
        await AssertSignedByProviderAsync(logoutToken!);

        captured.AppError.Should().BeNull(
            $"forwarding the request to the app failed: {Sanitize(captured.AppError)}");
        captured.AppAccepted.Should().BeTrue(
            $"the provider delivered a signed logout token to {captured.Method} " +
            $"{captured.PathAndQuery} (peer {captured.RemoteIpAddress}), but the app answered " +
            $"{captured.AppStatusCode}. App response: {Truncate(Sanitize(captured.AppBody), 400)}. " +
            "POST /oidc/backchannel-logout is implemented by the PR108 production lane; " +
            "provider-to-app delivery is proven, app acceptance is not.");
    }

    private void AssertLogoutTokenClaims(string? logoutToken)
    {
        logoutToken.Should().NotBeNullOrWhiteSpace(
            "Keycloak must deliver the back-channel logout as a logout_token form field");

        JsonWebToken token;
        try
        {
            token = new JsonWebToken(logoutToken);
        }
        catch (Exception ex)
        {
            Assert.Fail($"The delivered logout_token is not a readable JWT: {ex.GetType().Name}");
            return;
        }

        token.IsSigned.Should().BeTrue("the logout token must be signed");
        token.Alg.Should().NotBeNullOrWhiteSpace("the logout token header must carry an algorithm");

        token.Issuer.Should().Be(_fixture!.Authority, "iss must be the provider authority");
        token.Audiences.Should().Contain(
            KeycloakFixture.ClientId, "aud must identify the mqttprobe client");
        token.Subject.Should().NotBeNullOrWhiteSpace("sub must identify the logged-out user");
        token.Id.Should().NotBeNullOrWhiteSpace("jti must make the token unique");

        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        var root = payload.RootElement;
        var claimNames = string.Join(", ", root.EnumerateObject().Select(claim => claim.Name));

        root.TryGetProperty("sid", out var sid).Should().BeTrue(
            $"a session logout token must carry sid; claims: {claimNames}");
        sid.GetString().Should().NotBeNullOrWhiteSpace("sid must identify the provider session");

        root.TryGetProperty("events", out var events).Should().BeTrue(
            $"back-channel logout tokens must carry events; claims: {claimNames}");
        events
            .TryGetProperty("http://schemas.openid.net/event/backchannel-logout", out _)
            .Should().BeTrue($"events must contain the back-channel logout event; claims: {claimNames}");

        root.TryGetProperty("iat", out var issuedAt).Should().BeTrue(
            $"a logout token must carry iat; claims: {claimNames}");
        root.TryGetProperty("exp", out var expiresAt).Should().BeTrue(
            $"a logout token must carry exp; claims: {claimNames}");

        if (issuedAt.ValueKind == JsonValueKind.Number && expiresAt.ValueKind == JsonValueKind.Number)
        {
            expiresAt.GetInt64().Should().BeGreaterThan(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30, "the logout token must not be expired");
            expiresAt.GetInt64().Should().BeGreaterThan(issuedAt.GetInt64(), "exp must be after iat");
        }

        root.TryGetProperty("nonce", out _).Should().BeFalse(
            $"a logout token must never carry nonce; claims: {claimNames}");
    }

    private async Task AssertSignedByProviderAsync(string logoutToken)
    {
        var alg = new JsonWebToken(logoutToken).Alg;

        alg.Should().NotBeNullOrWhiteSpace("the logout token must be signed");

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = _fixture!.Authority,
            ValidAudiences = [KeycloakFixture.ClientId],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        if (alg.StartsWith("HS", StringComparison.Ordinal))
        {
            parameters.IssuerSigningKeys =
            [
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(KeycloakFixture.ClientSecret))
            ];
        }
        else
        {
            var configuration = await GetProviderConfigurationAsync();
            configuration.SigningKeys.Should().NotBeEmpty(
                "the provider must publish signing keys for its logout tokens");
            parameters.IssuerSigningKeys = configuration.SigningKeys;
        }

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(logoutToken, parameters);
        result.IsValid.Should().BeTrue(
            $"the logout token must verify against provider key material ({alg}), otherwise it " +
            $"did not come from Keycloak: {Sanitize(result.Exception?.Message)}");
    }

    private async Task<OpenIdConnectConfiguration> GetProviderConfigurationAsync()
    {
        var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{_fixture!.Authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = false });

        return await configurationManager.GetConfigurationAsync(CancellationToken.None);
    }

    private async Task AuthenticateAdmittedAsync(HttpClient appClient, OidcProtocolDriver driver)
    {
        var authorizationUri = await driver.BeginLoginAsync("/");
        var callback = await driver.SubmitProviderLoginAsync(
            authorizationUri,
            KeycloakFixture.AdmittedUsername,
            KeycloakFixture.Password);

        using var callbackResponse = await driver.SendAppCallbackAsync(callback);
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var location = callbackResponse.Headers.Location;
        location.Should().NotBeNull();

        using var followResponse = await appClient.GetAsync(location.OriginalString);
        followResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the admitted user must hold a live app session");
    }

    private (OidcWebApplicationFactory factory, HttpClient appClient, OidcProtocolDriver driver)
        CreateAppClientAndDriver()
    {
        var factory = new OidcWebApplicationFactory(
            _fixture!.Authority,
            KeycloakFixture.ClientId,
            KeycloakFixture.ClientSecret,
            KeycloakFixture.AdmissionClaim,
            KeycloakFixture.AcceptedValue);

        var appClient = factory.CreateOidcClient();
        var driver = new OidcProtocolDriver(appClient);

        return (factory, appClient, driver);
    }

    private static Uri ResolveLocation(Uri? location, Uri baseAddress)
    {
        location.Should().NotBeNull("Location header must be present");

        return location.IsAbsoluteUri ? location : new Uri(baseAddress, location);
    }

    // Never let a JWT reach test output, logs, or CI artifacts.
    private static string Sanitize(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : _jwtPattern.Replace(text, "[jwt redacted]");

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...";
}
