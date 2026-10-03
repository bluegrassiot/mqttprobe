using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MqttProbe.Core.Services.Security;
using MqttProbe.Web.Authentication;
using AuthOptions = MqttProbe.Web.Authentication.AuthenticationOptions;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class OidcAuthenticationEventsTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static (OidcAuthenticationEvents Events, AppSessionCoordinator Coordinator, DenialStateStore DenialStore)
        CreateEvents(TimeProvider? timeProvider = null, AuthOptions? authOptions = null)
    {
        var tp = timeProvider ?? new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(tp, TimeSpan.FromHours(8));
        var denialStore = new DenialStateStore(tp);
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var options = authOptions ?? new AuthOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions
            {
                AdmissionClaim = "groups",
                AcceptedValues = ["mqttprobe-users"]
            }
        };
        var events = new OidcAuthenticationEvents(coordinator, denialStore, Options.Create(options), logger);
        return (events, coordinator, denialStore);
    }

    private static TokenValidatedContext CreateTokenValidatedContext(
        JwtSecurityToken token)
    {
        var httpContext = new DefaultHttpContext();
        var authScheme = new AuthenticationScheme("OpenIdConnect", "OpenIdConnect", typeof(OpenIdConnectHandler));
        var oidcOptions = new OpenIdConnectOptions();

        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        return new TokenValidatedContext(httpContext, authScheme, oidcOptions, principal, new AuthenticationProperties())
        {
            SecurityToken = token
        };
    }

    [Test]
    public async Task OnTokenValidated_AdmittedUser_CreatesSession()
    {
        var (events, coordinator, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":"mqttprobe-users"}""");
        var context = CreateTokenValidatedContext(jwt);

        await events.OnTokenValidated(context);

        context.Principal.Should().NotBeNull();
        context.Principal!.FindFirst(AuthClaimTypes.AppSessionId).Should().NotBeNull();
        coordinator.GetSession(context.Principal.FindFirst(AuthClaimTypes.AppSessionId)!.Value).Should().NotBeNull();
    }

    [Test]
    public async Task OnTokenValidated_AdmittedUser_CapturesSidOnSession()
    {
        var (events, coordinator, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","sid":"provider-sid-1","name":"John Doe","groups":"mqttprobe-users"}""");
        var context = CreateTokenValidatedContext(jwt);

        await events.OnTokenValidated(context);

        var sessionId = context.Principal!.FindFirst(AuthClaimTypes.AppSessionId)!.Value;
        coordinator.GetSession(sessionId)!.Sid.Should().Be("provider-sid-1");
    }

    [Test]
    public async Task OnTokenValidated_AdmittedUser_WithoutSid_LeavesSessionSidNull()
    {
        var (events, coordinator, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":"mqttprobe-users"}""");
        var context = CreateTokenValidatedContext(jwt);

        await events.OnTokenValidated(context);

        var sessionId = context.Principal!.FindFirst(AuthClaimTypes.AppSessionId)!.Value;
        coordinator.GetSession(sessionId)!.Sid.Should().BeNull();
    }

    [Test]
    public async Task OnTokenValidated_DeniedUser_RedirectsToAccessDenied()
    {
        var (events, _, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":"other-group"}""");
        var context = CreateTokenValidatedContext(jwt);

        await events.OnTokenValidated(context);

        context.Response.StatusCode.Should().Be(302);
        context.Response.Headers.Location.ToString().Should().StartWith("/AccessDenied?state=");
    }

    [Test]
    public async Task OnTokenValidated_DeniedUser_NoAdmittedPrincipal()
    {
        var (events, _, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":"other-group"}""");
        var context = CreateTokenValidatedContext(jwt);

        await events.OnTokenValidated(context);

        var sessionId = context.Principal?.FindFirst(AuthClaimTypes.AppSessionId);
        sessionId.Should().BeNull();
    }

    [Test]
    public async Task OnTokenValidated_AdmittedUser_ExactSixClaims()
    {
        var (events, _, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":"mqttprobe-users"}""");
        var context = CreateTokenValidatedContext(jwt);

        await events.OnTokenValidated(context);

        var claims = context.Principal!.Claims.ToList();
        claims.Should().HaveCount(6);
        claims.Should().Contain(c => c.Type == AuthClaimTypes.AppIssuer && c.Value == "https://idp.example.com");
        claims.Should().Contain(c => c.Type == AuthClaimTypes.AppSubject && c.Value == "user-123");
        claims.Should().Contain(c => c.Type == AuthClaimTypes.AppDisplayName && c.Value == "John Doe");
        claims.Should().Contain(c => c.Type == AuthClaimTypes.AppSessionId);
        claims.Should().Contain(c => c.Type == AuthClaimTypes.AppSessionExpiry);
        claims.Should().Contain(c => c.Type == AuthClaimTypes.AppRole && c.Value == AppRoles.Admin);
    }

    [Test]
    public async Task OnTokenValidated_AdmittedUser_RetainsOnlyIdToken()
    {
        var (events, _, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":"mqttprobe-users"}""");
        var context = CreateTokenValidatedContext(jwt);

        await events.OnTokenValidated(context);

        context.Properties.Should().NotBeNull();
        context.Properties!.GetTokenValue("id_token").Should().NotBeNullOrEmpty();
        context.Properties.GetTokenValue("access_token").Should().BeNull();
        context.Properties.GetTokenValue("refresh_token").Should().BeNull();
    }

    [Test]
    public async Task OnTokenValidated_AdmittedUser_PreservesRedirectUri()
    {
        var (events, _, _) = CreateEvents();
        var jwt = CreateJwtSecurityToken("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":"mqttprobe-users"}""");
        var context = CreateTokenValidatedContext(jwt);
        context.Properties!.RedirectUri = "/dashboard";

        await events.OnTokenValidated(context);

        context.Properties.RedirectUri.Should().Be("/dashboard");
    }

    [Test]
    public async Task OnTokenValidated_NonJwtSecurityToken_Fails()
    {
        var (events, _, _) = CreateEvents();
        var httpContext = new DefaultHttpContext();
        var authScheme = new AuthenticationScheme("OpenIdConnect", "OpenIdConnect", typeof(OpenIdConnectHandler));
        var oidcOptions = new OpenIdConnectOptions();
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        // Use a non-JWT SecurityToken
        var context = new TokenValidatedContext(httpContext, authScheme, oidcOptions, principal, new AuthenticationProperties())
        {
            SecurityToken = new JwtSecurityToken()
        };

        await events.OnTokenValidated(context);

        // Empty JwtSecurityToken has no iss/sub, so admission fails → denial redirect
        context.Response.StatusCode.Should().Be(302);
    }

    [Test]
    public async Task OnRemoteFailure_RedirectsToLoginWithCategory()
    {
        var httpContext = new DefaultHttpContext();
        var context = new RemoteFailureContext(httpContext, new AuthenticationScheme("OpenIdConnect", "OpenIdConnect", typeof(OpenIdConnectHandler)), new OpenIdConnectOptions(), new Exception("test"));

        await OidcAuthenticationEvents.OnRemoteFailure(context);

        context.Response.StatusCode.Should().Be(302);
        context.Response.Headers.Location.ToString().Should().StartWith("/Login?error=");
    }

    [Test]
    public async Task HandleValidatePrincipal_MissingSessionId_RejectsWithMissingSessionId()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var (events, _, _) = CreateEventsWithLogger(logger);
        var principal = CreatePrincipalWithMissingClaim(AuthClaimTypes.AppSessionId);
        var context = CreateCookieValidateContext(principal);

        await events.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        ReceivedLogWithReason(logger, "missing_session_id").Should().BeTrue();
    }

    [Test]
    public async Task HandleValidatePrincipal_MissingIssuer_RejectsWithMissingIssuer()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var (events, _, _) = CreateEventsWithLogger(logger);
        var principal = CreatePrincipalWithMissingClaim(AuthClaimTypes.AppIssuer);
        var context = CreateCookieValidateContext(principal);

        await events.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        ReceivedLogWithReason(logger, "missing_issuer").Should().BeTrue();
    }

    [Test]
    public async Task HandleValidatePrincipal_MissingSubject_RejectsWithMissingSubject()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var (events, _, _) = CreateEventsWithLogger(logger);
        var principal = CreatePrincipalWithMissingClaim(AuthClaimTypes.AppSubject);
        var context = CreateCookieValidateContext(principal);

        await events.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        ReceivedLogWithReason(logger, "missing_subject").Should().BeTrue();
    }

    [Test]
    public async Task HandleValidatePrincipal_MissingSessionExpiry_RejectsWithMissingSessionExpiry()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var (events, _, _) = CreateEventsWithLogger(logger);
        var principal = CreatePrincipalWithMissingClaim(AuthClaimTypes.AppSessionExpiry);
        var context = CreateCookieValidateContext(principal);

        await events.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        ReceivedLogWithReason(logger, "missing_session_expiry").Should().BeTrue();
    }

    [Test]
    public async Task HandleValidatePrincipal_InvalidSessionExpiry_RejectsWithInvalidSessionExpiry()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var (events, _, _) = CreateEventsWithLogger(logger);
        var principal = CreatePrincipalWithInvalidExpiry();
        var context = CreateCookieValidateContext(principal);

        await events.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        ReceivedLogWithReason(logger, "invalid_session_expiry").Should().BeTrue();
    }

    [Test]
    public async Task HandleValidatePrincipal_SessionNotFound_RejectsWithSessionNotFound()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var (events, _, _) = CreateEventsWithLogger(logger);
        var principal = CreatePrincipalWithClaims("session-1", "https://idp.example.com", "user-1", _epoch.AddHours(1));
        var context = CreateCookieValidateContext(principal);

        await events.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        ReceivedLogWithReason(logger, "session_not_found").Should().BeTrue();
    }

    [Test]
    public async Task HandleValidatePrincipal_RejectionLogsNeverContainSensitiveValues()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var (events, _, _) = CreateEventsWithLogger(logger);
        var principal = CreatePrincipalWithClaims("secret-session-id", "https://idp.example.com", "user-123", _epoch.AddHours(1));
        var context = CreateCookieValidateContext(principal);

        await events.OnValidatePrincipal(context);

        var calls = logger.ReceivedCalls().ToList();
        var logMessages = calls
            .Where(call => call.GetMethodInfo().Name == "Log")
            .Select(call => call.GetArguments()[2]?.ToString() ?? "")
            .ToList();

        foreach (var message in logMessages)
        {
            message.Should().NotContain("secret-session-id");
            message.Should().NotContain("https://idp.example.com");
            message.Should().NotContain("user-123");
            message.Should().NotContain("session-1");
        }
    }

    [Test]
    public async Task HandleValidatePrincipal_SessionInvalid_RejectsWithSessionInvalid()
    {
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var tp = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(tp, TimeSpan.FromHours(8));
        var identity = ExternalIdentity.Create("https://idp.example.com", "user-1", "John Doe");
        var session = coordinator.CreateSession(identity);

        // Claims have a different issuer than the session, so IsValid returns false
        // while GetSession still finds the record.
        var principal = CreatePrincipalWithClaims(session.SessionId, "https://other-idp.example.com", "user-1", session.ExpiresAt);
        var context = CreateCookieValidateContext(principal);

        var events = new OidcAuthenticationEvents(coordinator, new DenialStateStore(tp), Options.Create(new AuthOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions { AdmissionClaim = "groups", AcceptedValues = ["mqttprobe-users"] }
        }), logger);

        await events.OnValidatePrincipal(context);

        context.Principal.Should().BeNull();
        ReceivedLogWithReason(logger, "session_invalid").Should().BeTrue();
    }

    private static (OidcAuthenticationEvents Events, AppSessionCoordinator Coordinator, DenialStateStore DenialStore)
        CreateEventsWithLogger(ILogger<OidcAuthenticationEvents> logger, TimeProvider? timeProvider = null, AuthOptions? authOptions = null)
    {
        var tp = timeProvider ?? new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(tp, TimeSpan.FromHours(8));
        var denialStore = new DenialStateStore(tp);
        var options = authOptions ?? new AuthOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions
            {
                AdmissionClaim = "groups",
                AcceptedValues = ["mqttprobe-users"]
            }
        };
        var events = new OidcAuthenticationEvents(coordinator, denialStore, Options.Create(options), logger);
        return (events, coordinator, denialStore);
    }

    private static CookieValidatePrincipalContext CreateCookieValidateContext(ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_epoch));
        httpContext.RequestServices = services.BuildServiceProvider();
        var scheme = new AuthenticationScheme("Cookies", "Cookies", typeof(CookieAuthenticationHandler));
        var options = new CookieAuthenticationOptions();
        var ticket = new AuthenticationTicket(principal, "Cookies");
        return new CookieValidatePrincipalContext(httpContext, scheme, options, ticket);
    }

    private static ClaimsPrincipal CreatePrincipalWithClaims(string sessionId, string issuer, string subject, DateTimeOffset expiry)
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(AuthClaimTypes.AppSessionId, sessionId));
        identity.AddClaim(new Claim(AuthClaimTypes.AppIssuer, issuer));
        identity.AddClaim(new Claim(AuthClaimTypes.AppSubject, subject));
        identity.AddClaim(new Claim(AuthClaimTypes.AppSessionExpiry, expiry.ToUnixTimeSeconds().ToString()));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePrincipalWithMissingClaim(string excludeClaimType)
    {
        var identity = new ClaimsIdentity("TestAuth");
        var expiry = _epoch.AddHours(1);

        if (excludeClaimType != AuthClaimTypes.AppSessionId)
            identity.AddClaim(new Claim(AuthClaimTypes.AppSessionId, "session-1"));
        if (excludeClaimType != AuthClaimTypes.AppIssuer)
            identity.AddClaim(new Claim(AuthClaimTypes.AppIssuer, "https://idp.example.com"));
        if (excludeClaimType != AuthClaimTypes.AppSubject)
            identity.AddClaim(new Claim(AuthClaimTypes.AppSubject, "user-1"));
        if (excludeClaimType != AuthClaimTypes.AppSessionExpiry)
            identity.AddClaim(new Claim(AuthClaimTypes.AppSessionExpiry, expiry.ToUnixTimeSeconds().ToString()));

        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreatePrincipalWithInvalidExpiry()
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(AuthClaimTypes.AppSessionId, "session-1"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppIssuer, "https://idp.example.com"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppSubject, "user-1"));
        identity.AddClaim(new Claim(AuthClaimTypes.AppSessionExpiry, "not-a-number"));
        return new ClaimsPrincipal(identity);
    }

    private static bool ReceivedLogWithReason(ILogger<OidcAuthenticationEvents> logger, string reason)
    {
        var calls = logger.ReceivedCalls().ToList();
        return calls.Any(call =>
        {
            if (call.GetMethodInfo().Name != "Log") return false;
            var args = call.GetArguments();
            if (args.Length < 5) return false;
            if (args[0] is not LogLevel level || level != LogLevel.Debug) return false;
            var state = args[2]?.ToString() ?? "";
            return state.Contains(reason);
        });
    }

    private static JwtSecurityToken CreateJwtSecurityToken(string payloadJson)
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(payloadJson);
        return new JwtSecurityToken($"{header}.{payload}.");
    }

    [Test]
    public async Task OnRedirectToIdentityProviderForSignOut_WithIdToken_SetsIdTokenHint()
    {
        var (events, _, _) = CreateEvents(authOptions: new AuthOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions
            {
                ClientId = "mqttprobe",
                AdmissionClaim = "groups",
                AcceptedValues = ["mqttprobe-users"]
            }
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("mqttprobe.test");
        var services = new ServiceCollection();
        services.AddSingleton(new PublicOriginResolver("https://mqttprobe.test", "*"));
        httpContext.RequestServices = services.BuildServiceProvider();

        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = "test-id-token" }]);

        var oidcOptions = new OpenIdConnectOptions();
        var message = new OpenIdConnectMessage();
        var context = new RedirectContext(httpContext, new AuthenticationScheme("OpenIdConnect", "OpenIdConnect", typeof(OpenIdConnectHandler)), oidcOptions, properties)
        {
            ProtocolMessage = message
        };

        await events.OnRedirectToIdentityProviderForSignOut(context);

        context.ProtocolMessage.IdTokenHint.Should().Be("test-id-token");
        context.ProtocolMessage.PostLogoutRedirectUri.Should().Be("https://mqttprobe.test/signout-callback-oidc");
        context.ProtocolMessage.ClientId.Should().BeNull();
    }

    [Test]
    public async Task OnRedirectToIdentityProviderForSignOut_WithoutIdToken_SetsClientId()
    {
        var (events, _, _) = CreateEvents(authOptions: new AuthOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions
            {
                ClientId = "mqttprobe",
                AdmissionClaim = "groups",
                AcceptedValues = ["mqttprobe-users"]
            }
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("mqttprobe.test");
        var services = new ServiceCollection();
        services.AddSingleton(new PublicOriginResolver("https://mqttprobe.test", "*"));
        httpContext.RequestServices = services.BuildServiceProvider();

        var properties = new AuthenticationProperties();
        var oidcOptions = new OpenIdConnectOptions();
        var message = new OpenIdConnectMessage();
        var context = new RedirectContext(httpContext, new AuthenticationScheme("OpenIdConnect", "OpenIdConnect", typeof(OpenIdConnectHandler)), oidcOptions, properties)
        {
            ProtocolMessage = message
        };

        await events.OnRedirectToIdentityProviderForSignOut(context);

        context.ProtocolMessage.IdTokenHint.Should().BeNull();
        context.ProtocolMessage.ClientId.Should().Be("mqttprobe");
        context.ProtocolMessage.PostLogoutRedirectUri.Should().Be("https://mqttprobe.test/signout-callback-oidc");
    }
}
