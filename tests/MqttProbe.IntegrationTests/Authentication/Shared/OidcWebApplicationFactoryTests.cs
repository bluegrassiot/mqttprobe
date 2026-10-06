using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Web;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MqttProbe.Web.Authentication;

namespace MqttProbe.IntegrationTests.Authentication;

[TestFixture]
public class OidcWebApplicationFactoryTests
{
    private const string Authority = "http://127.0.0.1:65534/realms/test";
    private const string ClientId = "mqttprobe";
    private const string ClientSecret = "test-secret";
    private const string AdmissionClaim = "mqttprobe_access";
    private const string AcceptedValue = "admin";

    [Test]
    public async Task LoginPage_OidcMode_ReturnsOidcButtonWithoutLocalControls()
    {
        await using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        using var response = await client.GetAsync("/Login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("Sign in with Keycloak");

        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);

        document.QuerySelector("input#username").Should().BeNull(
            "OIDC login page must not expose local username input");
        document.QuerySelector("input#password").Should().BeNull(
            "OIDC login page must not expose local password input");
    }

    [Test]
    public async Task LoginPage_OidcMode_ChallengeFormRendersActionPath()
    {
        await using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        using var response = await client.GetAsync("/Login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();
        var document = new HtmlParser().ParseDocument(html);

        var forms = document.QuerySelectorAll("form");
        forms.Should().HaveCount(1, "OIDC login page must contain exactly one form");

        var form = forms[0];
        form.GetAttribute("method").Should().Be("post",
            "challenge form must use POST");

        var action = form.GetAttribute("action");
        action.Should().NotBeNullOrWhiteSpace(
            "challenge form must have a rendered action (tag helpers must be active)");

        var resolvedAction = new Uri(new Uri("https://mqttprobe.test"), action!);
        resolvedAction.AbsolutePath.Should().Be("/Login",
            "challenge form action must target the Login page");
        resolvedAction.Query.Should().Contain("handler=Challenge",
            "challenge form action must include the Challenge handler query");

        form.GetAttribute("asp-page-handler").Should().BeNull(
            "asp-page-handler must be consumed by the tag helper, not left as an inert attribute");
    }

    [Test]
    public void ResolvedOptions_OidcMode_BindsExpectedValues()
    {
        using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        var options = factory.Services
            .GetRequiredService<IOptions<AuthenticationOptions>>()
            .Value;

        options.Mode.Should().Be("OIDC");
        options.Oidc.Authority.Should().Be(Authority);
        options.Oidc.PublicBaseUrl.Should().Be("https://mqttprobe.test");
        options.Oidc.AdmissionClaim.Should().Be(AdmissionClaim);
        options.Oidc.AcceptedValues.Should().ContainSingle(AcceptedValue);
    }

    [Test]
    public void OidcSchemeOptions_ProgramConfigured_SetsAuthorityAndClient()
    {
        using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        var oidcOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        oidcOptions.Authority.Should().Be(Authority);
        oidcOptions.ClientId.Should().Be(ClientId);
        oidcOptions.ClientSecret.Should().Be(ClientSecret);
        oidcOptions.RequireHttpsMetadata.Should().BeFalse();
        oidcOptions.ResponseType.Should().Be("code");
        oidcOptions.ResponseMode.Should().Be("form_post");
        oidcOptions.UsePkce.Should().BeTrue();
        oidcOptions.CallbackPath.Value.Should().Be("/signin-oidc");
    }

    [Test]
    public void OidcBranchServices_Registered_ProveProgramTookOidcPath()
    {
        using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        factory.Services.GetService<AppSessionCoordinator>().Should().NotBeNull();
        factory.Services.GetService<AuthenticationConfigurator>().Should().NotBeNull();
    }

    [Test]
    public void ClaimActions_OidcMode_IsEmpty()
    {
        using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        var oidcOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        oidcOptions.ClaimActions.Should().BeEmpty(
            "Program.cs must call ClaimActions.Clear() so the OIDC handler does not strip app claims");
    }

    [Test]
    public void ClaimActions_OidcMode_AllSixMinimalAppClaimsSurvive()
    {
        using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        var oidcOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        // Simulate the minimal principal that OnTokenValidated creates
        var identity = new ClaimsIdentity("OIDC");
        identity.AddClaim(new Claim(AuthClaimTypes.AppIssuer, "test-issuer"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppSubject, "test-subject"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppDisplayName, "Test User"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppSessionId, "session-123"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppSessionExpiry, "1234567890"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppRole, "Admin"));

        // Apply each claim action (no-UserInfo path: token claims only)
        using var doc = JsonDocument.Parse("{}");
        foreach (var action in oidcOptions.ClaimActions)
        {
            action.Run(doc.RootElement, identity, "test-issuer");
        }

        identity.FindFirst(AuthClaimTypes.AppIssuer).Should().NotBeNull(
            "ClaimActions must not delete the AppIssuer claim");
        identity.FindFirst(AuthClaimTypes.AppSubject).Should().NotBeNull(
            "ClaimActions must not delete the AppSubject claim");
        identity.FindFirst(AuthClaimTypes.AppDisplayName).Should().NotBeNull(
            "ClaimActions must not delete the AppDisplayName claim");
        identity.FindFirst(AuthClaimTypes.AppSessionId).Should().NotBeNull(
            "ClaimActions must not delete the AppSessionId claim");
        identity.FindFirst(AuthClaimTypes.AppSessionExpiry).Should().NotBeNull(
            "ClaimActions must not delete the AppSessionExpiry claim");
        identity.FindFirst(AuthClaimTypes.AppRole).Should().NotBeNull(
            "ClaimActions must not delete the AppRole claim");

        identity.FindFirst(AuthClaimTypes.AppIssuer)!.Value.Should().Be("test-issuer");
        identity.FindFirst(AuthClaimTypes.AppSubject)!.Value.Should().Be("test-subject");
        identity.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("Test User");
        identity.FindFirst(AuthClaimTypes.AppSessionId)!.Value.Should().Be("session-123");
        identity.FindFirst(AuthClaimTypes.AppSessionExpiry)!.Value.Should().Be("1234567890");
        identity.FindFirst(AuthClaimTypes.AppRole)!.Value.Should().Be("Admin");
    }

    [Test]
    public async Task AccessDeniedPage_LogoutFormRendersActionPath()
    {
        await using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        var denialStore = factory.Services.GetRequiredService<DenialStateStore>();
        var state = denialStore.Store("test-user", "denied");

        using var response = await client.GetAsync($"/AccessDenied?state={state}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();
        var document = new HtmlParser().ParseDocument(html);

        var logoutForm = document.QuerySelector("form[action*='/Logout']");
        logoutForm.Should().NotBeNull("AccessDenied page must contain a logout form");

        logoutForm!.GetAttribute("method").Should().Be("post",
            "logout form must use POST");

        var action = logoutForm.GetAttribute("action");
        action.Should().NotBeNullOrWhiteSpace(
            "logout form must have a rendered action (tag helpers must be active)");

        var resolvedAction = new Uri(new Uri("https://mqttprobe.test"), action!);
        resolvedAction.AbsolutePath.Should().Be("/Logout",
            "logout form action must target the Logout page");

        logoutForm.GetAttribute("asp-page").Should().BeNull(
            "asp-page must be consumed by the tag helper, not left as an inert attribute");

        logoutForm.QuerySelector("input[name='__RequestVerificationToken']").Should().NotBeNull(
            "logout form must preserve antiforgery token");
    }

    [Test]
    public async Task AccessDeniedPage_BothFormsAreLogoutPostsWithAntiforgery()
    {
        await using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        var denialStore = factory.Services.GetRequiredService<DenialStateStore>();
        var state = denialStore.Store("test-user", "denied");

        using var response = await client.GetAsync($"/AccessDenied?state={state}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();
        var document = new HtmlParser().ParseDocument(html);

        var forms = document.QuerySelectorAll("form.denial-action-form");
        forms.Should().HaveCount(2,
            "AccessDenied page must contain exactly two action forms");

        foreach (var form in forms)
        {
            form.GetAttribute("method").Should().Be("post",
                "each action form must use POST");

            var action = form.GetAttribute("action");
            action.Should().NotBeNullOrWhiteSpace(
                "each action form must have a rendered action");

            var resolvedAction = new Uri(new Uri("https://mqttprobe.test"), action!);
            resolvedAction.AbsolutePath.Should().Be("/Logout",
                "each action form must target the Logout page");

            form.QuerySelector("input[name='__RequestVerificationToken']").Should().NotBeNull(
                "each action form must preserve antiforgery token");

            form.QuerySelector("input[name='forceLogin']").Should().BeNull(
                "action forms must not contain a forceLogin field");
            form.QuerySelector("input[name='returnUrl']").Should().BeNull(
                "action forms must not contain a returnUrl field");
            form.QuerySelector("input[name='username']").Should().BeNull(
                "action forms must not contain a username field");
            form.QuerySelector("input[name='password']").Should().BeNull(
                "action forms must not contain a password field");
        }
    }

    [Test]
    public async Task OidcChallenge_WhenProviderMetadataUnavailable_ReturnsSafeLoginError()
    {
        const string unreachableAuthority = "http://127.0.0.1:1";

        await using var factory = new OidcWebApplicationFactory(
            unreachableAuthority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue);

        using var client = factory.CreateOidcClient();

        using var getResponse = await client.GetAsync("/Login?returnUrl=%2F");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await getResponse.Content.ReadAsStringAsync();
        var document = new HtmlParser().ParseDocument(html);

        var challengeForm = document.QuerySelector("form");
        challengeForm.Should().NotBeNull("login page must contain a form");

        challengeForm!.QuerySelector(".oidc-button").Should().NotBeNull(
            "form must contain an OIDC button proving it is the OIDC challenge form");

        var formData = new List<KeyValuePair<string, string>>();
        foreach (var input in challengeForm.QuerySelectorAll("input"))
        {
            if (input.HasAttribute("disabled"))
                continue;

            var name = input.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;

            formData.Add(new KeyValuePair<string, string>(name, input.GetAttribute("value") ?? ""));
        }

        formData.Should().Contain(kv => kv.Key == "__RequestVerificationToken",
            "challenge form must include antiforgery token");
        formData.Should().Contain(kv => kv.Key == "returnUrl" && kv.Value == "/",
            "challenge form must preserve returnUrl");

        var renderedAction = challengeForm.GetAttribute("action");
        renderedAction.Should().NotBeNullOrWhiteSpace(
            "challenge form must have a rendered action (tag helpers must be active)");

        var resolvedAction = new Uri(OidcWebApplicationFactory._appOrigin, renderedAction!);
        resolvedAction.AbsolutePath.Should().Be("/Login",
            "rendered action must target the Login page");
        resolvedAction.Query.Should().Contain("handler=Challenge",
            "rendered action must include the Challenge handler query");

        using var postContent = new FormUrlEncodedContent(formData);
        using var postResponse = await client.PostAsync(renderedAction, postContent);

        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "challenge POST should redirect when provider metadata is unreachable");

        var location = postResponse.Headers.Location;
        location.Should().NotBeNull("redirect must include Location header");

        var resolvedLocation = location!.IsAbsoluteUri
            ? location
            : new Uri(OidcWebApplicationFactory._appOrigin, location);

        resolvedLocation.AbsolutePath.Should().Contain("/Login",
            "redirect target must be the Login page");

        var queryParams = HttpUtility.ParseQueryString(resolvedLocation.Query);
        queryParams.Get("error").Should().Be("provider_unavailable",
            "redirect must carry the safe provider_unavailable error category");

        resolvedLocation.ToString().Should().NotContain(unreachableAuthority,
            "Location must not leak the unreachable authority");
        resolvedLocation.ToString().Should().NotContain(ClientSecret,
            "Location must not leak the client secret");
        resolvedLocation.ToString().Should().NotContain("HttpRequestException",
            "Location must not leak exception type names");

        using var followResponse = await client.GetAsync(resolvedLocation);
        followResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var followHtml = await followResponse.Content.ReadAsStringAsync();
        followHtml.Should().Contain("temporarily unavailable",
            "Login page must show the provider-unavailable message");
        followHtml.Should().NotContain(unreachableAuthority,
            "Login page must not leak the unreachable authority");
        followHtml.Should().NotContain(ClientSecret,
            "Login page must not leak the client secret");
        followHtml.Should().NotContain("HttpRequestException",
            "Login page must not leak exception type names");
    }

    [Test]
    public async Task HostFiltering_NoPublicBaseUrl_EvilHost_Returns400()
    {
        await using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue,
            publicBaseUrl: null,
            allowedHosts: "mqttprobe.test");

        using var client = factory.CreateOidcClient();
        client.DefaultRequestHeaders.Host = "evil.test";

        using var response = await client.GetAsync("/Login");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "production HostFiltering middleware must reject requests with disallowed Host headers");
    }

    [Test]
    public async Task Challenge_PublicBaseUrl_EvilHost_RedirectUriUsesConfiguredBase()
    {
        var staticConfig = new OpenIdConnectConfiguration
        {
            AuthorizationEndpoint = "http://127.0.0.1:65534/realms/test/protocol/openid-connect/auth",
            TokenEndpoint = "http://127.0.0.1:65534/realms/test/protocol/openid-connect/token",
            Issuer = "http://127.0.0.1:65534/realms/test",
        };
        staticConfig.SigningKeys.Add(
            new Microsoft.IdentityModel.Tokens.RsaSecurityKey(
                System.Security.Cryptography.RSA.Create(2048)));

        await using var factory = new OidcWebApplicationFactory(
            Authority, ClientId, ClientSecret, AdmissionClaim, AcceptedValue,
            publicBaseUrl: "https://mqttprobe.test",
            allowedHosts: "*",
            staticConfiguration: staticConfig);

        using var client = factory.CreateOidcClient();
        client.DefaultRequestHeaders.Host = "evil.test";

        using var getResponse = await client.GetAsync("/Login");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await getResponse.Content.ReadAsStringAsync();
        var document = new HtmlParser().ParseDocument(html);

        var challengeForm = document.QuerySelector("form");
        challengeForm.Should().NotBeNull("login page must contain a challenge form");

        var formData = ParseFormInputs(challengeForm!);
        formData.Should().Contain(kv => kv.Key == "__RequestVerificationToken");

        var renderedAction = challengeForm!.GetAttribute("action");
        renderedAction.Should().NotBeNullOrWhiteSpace(
            "challenge form must have a rendered action (tag helpers must be active)");

        using var postContent = new FormUrlEncodedContent(formData);
        using var postResponse = await client.PostAsync(renderedAction, postContent);

        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "challenge should redirect to the provider authorization endpoint");

        var location = postResponse.Headers.Location;
        location.Should().NotBeNull();

        var resolvedLocation = location!.IsAbsoluteUri
            ? location
            : new Uri(OidcWebApplicationFactory._appOrigin, location);

        resolvedLocation.AbsolutePath.Should().Be("/realms/test/protocol/openid-connect/auth",
            "redirect must target the static provider authorization endpoint");

        var queryParams = HttpUtility.ParseQueryString(resolvedLocation.Query);
        queryParams.Get("redirect_uri").Should().Be("https://mqttprobe.test/signin-oidc",
            "redirect_uri must use the configured PublicBaseUrl, never the request Host");

        resolvedLocation.ToString().Should().NotContain("evil.test",
            "Location must not leak the evil host");

        resolvedLocation.ToString().Should().NotContain(ClientSecret,
            "Location must not leak the client secret");

        postResponse.Content.Headers.Should().NotContain(h =>
            h.Value.Any(v => v!.Contains(ClientSecret)),
            "response body must not leak the client secret");
    }

    private static List<KeyValuePair<string, string>> ParseFormInputs(
        AngleSharp.Dom.IElement form)
    {
        var data = new List<KeyValuePair<string, string>>();
        foreach (var input in form.QuerySelectorAll("input"))
        {
            if (input.HasAttribute("disabled"))
                continue;

            var name = input.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;

            data.Add(new KeyValuePair<string, string>(name, input.GetAttribute("value") ?? ""));
        }

        return data;
    }
}
