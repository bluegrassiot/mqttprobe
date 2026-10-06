using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MqttProbe.Web.Authentication;

public sealed class OidcAuthenticationEvents
{
    private readonly AppSessionCoordinator _coordinator;
    private readonly DenialStateStore _denialStore;
    private readonly AuthenticationOptions _authOptions;
    private readonly ILogger<OidcAuthenticationEvents> _logger;

    public OidcAuthenticationEvents(
        AppSessionCoordinator coordinator,
        DenialStateStore denialStore,
        IOptions<AuthenticationOptions> authOptions,
        ILogger<OidcAuthenticationEvents> logger)
    {
        _coordinator = coordinator;
        _denialStore = denialStore;
        _authOptions = authOptions.Value;
        _logger = logger;
    }

    public Func<TokenValidatedContext, Task> OnTokenValidated => HandleTokenValidated;
    public static Func<RemoteFailureContext, Task> OnRemoteFailure => HandleRemoteFailure;
    public Func<CookieValidatePrincipalContext, Task> OnValidatePrincipal => HandleValidatePrincipal;
    public static Func<RedirectContext, Task> OnRedirectToIdentityProvider => HandleRedirectToIdentityProvider;
    public Func<RedirectContext, Task> OnRedirectToIdentityProviderForSignOut => HandleRedirectToIdentityProviderForSignOut;

    private Task HandleTokenValidated(TokenValidatedContext context)
    {
        try
        {
            return ProcessTokenValidated(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            context.Fail("Token validation failed.");
            return Task.CompletedTask;
        }
    }

    private async Task ProcessTokenValidated(TokenValidatedContext context)
    {
        var httpContext = context.HttpContext;

        var extracted = ExtractTokenPayload(context);
        if (extracted is null)
            return;

        var (encodedToken, payload) = extracted.Value;

        var admissionClaim = _authOptions.Oidc.AdmissionClaim ?? "";
        var acceptedValues = _authOptions.Oidc.AcceptedValues;

        if (!AdmissionEvaluator.IsAdmitted(payload, admissionClaim, acceptedValues))
        {
            HandleDenial(context, payload);
            payload.Dispose();
            return;
        }

        await HandleAdmissionAsync(context, httpContext, payload, encodedToken);
        payload.Dispose();
    }

    private (string encodedToken, ValidatedIdTokenPayload payload)? ExtractTokenPayload(TokenValidatedContext context)
    {
        // TokenValidatedContext.SecurityToken is declared JwtSecurityToken but the
        // OIDC handler may set a JsonWebToken at runtime. Cast to object to bypass
        // compile-time type narrowing.
        var securityTokenObj = (object?)context.SecurityToken;

        if (securityTokenObj is JsonWebToken jsonWebToken)
        {
            try
            {
                var payload = ValidatedIdTokenPayload.CreateFromToken(jsonWebToken);
                return (jsonWebToken.EncodedToken, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to validate token payload");
                context.Fail("Invalid token payload.");
                return null;
            }
        }

        if (context.SecurityToken is { } legacyToken)
        {
            try
            {
                var payload = ValidatedIdTokenPayload.CreateFromRawJson(legacyToken.Payload.SerializeToJson());
                return (legacyToken.RawData, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to validate token payload");
                context.Fail("Invalid token payload.");
                return null;
            }
        }

        _logger.LogWarning("Token is not a recognized JWT type");
        context.Fail("Invalid token type.");
        return null;
    }

    private void HandleDenial(TokenValidatedContext context, ValidatedIdTokenPayload payload)
    {
        var displayName = AdmissionEvaluator.GetDisplayName(payload);
        var handle = _denialStore.Store(displayName, "admission_denied");

        context.Response.StatusCode = 302;
        context.Response.Headers.Location = $"/AccessDenied?state={Uri.EscapeDataString(handle)}";
        context.HandleResponse();
    }

    private async Task HandleAdmissionAsync(
        TokenValidatedContext context,
        HttpContext httpContext,
        ValidatedIdTokenPayload payload,
        string encodedToken)
    {
        // Preserve existing properties (RedirectUri, items) before replacement
        var existingRedirectUri = context.Properties?.RedirectUri;
        var existingItems = context.Properties?.Items.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // Await prior-session revocation before creating replacement
        var existingSessionId = httpContext.User.FindFirst(AuthClaimTypes.AppSessionId)?.Value;
        if (!string.IsNullOrEmpty(existingSessionId))
        {
            await _coordinator.RevokeSessionAsync(existingSessionId, httpContext.RequestAborted);
        }

        var issuer = GetStringProperty(payload.Root, AuthClaimTypes.Issuer);
        var subject = GetStringProperty(payload.Root, AuthClaimTypes.AppSubject);
        var displayName = AdmissionEvaluator.GetDisplayName(payload);
        var identity = ExternalIdentity.Create(issuer, subject, displayName);

        // sid is what a back-channel logout token will name later.
        var sid = GetStringProperty(payload.Root, AuthClaimTypes.Sid);
        var session = _coordinator.CreateSession(identity, sid.Length == 0 ? null : sid);

        var issuedAt = context.Properties?.IssuedUtc ?? DateTimeOffset.UtcNow;
        var expiresAt = session.ExpiresAt;
        var ticket = AuthenticationTicketFactory.Create(
            payload, session.SessionId, issuedAt, expiresAt, encodedToken);

        context.Principal = ticket.Principal;
        context.Properties = ticket.Properties;

        if (existingRedirectUri is not null)
            context.Properties.RedirectUri = existingRedirectUri;
        if (existingItems is not null)
        {
            foreach (var item in existingItems)
            {
                context.Properties.Items[item.Key] = item.Value;
            }
        }
    }

    private static Task HandleRemoteFailure(RemoteFailureContext context)
    {
        // Map safe remote/auth failures to bounded categories for Login
        var category = context.Failure switch
        {
            HttpRequestException => "provider_unavailable",
            OperationCanceledException => "timeout",
            _ => "authentication_failed"
        };

        context.Response.Redirect($"/Login?error={Uri.EscapeDataString(category)}");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private const string RejectReasonLog = "Cookie principal rejected: {Reason}";

    private Task HandleValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var sessionId = context.Principal?.FindFirst(AuthClaimTypes.AppSessionId)?.Value;
        var issuer = context.Principal?.FindFirst(AuthClaimTypes.AppIssuer)?.Value;
        var subject = context.Principal?.FindFirst(AuthClaimTypes.AppSubject)?.Value;
        var expiryStr = context.Principal?.FindFirst(AuthClaimTypes.AppSessionExpiry)?.Value;

        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogDebug(RejectReasonLog, "missing_session_id");
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(issuer))
        {
            _logger.LogDebug(RejectReasonLog, "missing_issuer");
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(subject))
        {
            _logger.LogDebug(RejectReasonLog, "missing_subject");
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(expiryStr))
        {
            _logger.LogDebug(RejectReasonLog, "missing_session_expiry");
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        if (!long.TryParse(expiryStr, out _))
        {
            _logger.LogDebug(RejectReasonLog, "invalid_session_expiry");
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

        var session = _coordinator.GetSession(sessionId);
        if (session is null)
        {
            _logger.LogDebug(RejectReasonLog, "session_not_found");
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        if (!session.IsValid(sessionId, issuer, subject, now))
        {
            _logger.LogDebug(RejectReasonLog, "session_invalid");
            context.RejectPrincipal();
        }

        return Task.CompletedTask;
    }

    private static Task HandleRedirectToIdentityProvider(RedirectContext context)
    {
        var originResolver = context.HttpContext.RequestServices.GetRequiredService<PublicOriginResolver>();
        var origin = originResolver.ResolveOrigin(context.Request);
        context.ProtocolMessage.RedirectUri = new Uri(origin, "/signin-oidc").ToString();
        return Task.CompletedTask;
    }

    private Task HandleRedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        var originResolver = context.HttpContext.RequestServices.GetRequiredService<PublicOriginResolver>();
        var origin = originResolver.ResolveOrigin(context.Request);
        context.ProtocolMessage.PostLogoutRedirectUri = new Uri(origin, "/signout-callback-oidc").ToString();

        // Set id_token_hint from retained token
        var idToken = context.Properties.GetTokenValue("id_token");
        if (!string.IsNullOrEmpty(idToken))
        {
            context.ProtocolMessage.IdTokenHint = idToken;
        }
        else
        {
            // No id_token hint - set client_id so the provider can identify the RP
            context.ProtocolMessage.ClientId = _authOptions.Oidc.ClientId;
        }

        return Task.CompletedTask;
    }

    private static string GetStringProperty(System.Text.Json.JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return element.GetString() ?? "";
        }

        return "";
    }
}
