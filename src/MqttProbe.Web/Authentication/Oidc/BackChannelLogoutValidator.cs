using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MqttProbe.Web.Authentication;

public sealed class BackChannelLogoutValidator
{
#pragma warning disable S5332 // The spec's event URI identifies a claim key, it is never fetched
    public const string LogoutEventType = "http://schemas.openid.net/event/backchannel-logout";
#pragma warning restore S5332

    private static readonly TimeSpan _clockSkew = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan _maxTokenAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _maxTokenLifetime = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan _metadataRefreshInterval = TimeSpan.FromSeconds(60);

    // Asymmetric algorithms only. 'none' is never eligible, and HMAC is excluded
    // outright: metadata never publishes the symmetric key that would verify an
    // HS* token, so allowing it would only widen the header attack surface.
    private static readonly string[] _allowedSigningAlgorithms =
    [
        "RS256", "RS384", "RS512",
        "PS256", "PS384", "PS512",
        "ES256", "ES384", "ES512"
    ];

    private readonly IOptionsMonitor<OpenIdConnectOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackChannelLogoutValidator> _logger;
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private long _lastMetadataRefreshUnixSeconds;

    public BackChannelLogoutValidator(
        IOptionsMonitor<OpenIdConnectOptions> options,
        TimeProvider timeProvider,
        ILogger<BackChannelLogoutValidator> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<BackChannelLogoutValidationResult> ValidateAsync(
        string logoutToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logoutToken))
        {
            return Reject("missing_token");
        }

        // Program.cs configures OpenIdConnectOptions under the named scheme; the
        // default-name options are never configured and would never validate.
        var options = _options.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configuration = await ResolveMetadataAsync(options, cancellationToken).ConfigureAwait(false);
        if (!TryCreateValidationParameters(options, configuration, out var rejection, out var trustedIssuer, out var parameters))
        {
            return Reject(rejection);
        }

        if (!TryParseAllowedToken(
                logoutToken, parameters, out var parsedToken, out var parseReason, out var parseError))
        {
            return Reject(parseReason!, parseError);
        }

        var (token, error) = await ValidateSignatureAsync(logoutToken, parameters).ConfigureAwait(false);

        // An unknown kid usually means the provider rotated its signing keys.
        if (token is null && error is SecurityTokenSignatureKeyNotFoundException)
        {
            var refreshed = await RefreshMetadataAsync(options, cancellationToken).ConfigureAwait(false);
            if (refreshed is not null &&
                TryCreateValidationParameters(options, refreshed, out rejection, out trustedIssuer, out parameters))
            {
                if (!IsAllowedAlgorithm(parsedToken.Alg, parameters))
                {
                    return Reject("disallowed_algorithm");
                }

                (token, error) = await ValidateSignatureAsync(logoutToken, parameters).ConfigureAwait(false);
            }
        }

        if (token is null)
        {
            return Reject(Classify(error), error?.GetType());
        }

        ValidatedIdTokenPayload payload;
        try
        {
            payload = ValidatedIdTokenPayload.CreateFromToken(token);
        }
        catch (ArgumentException ex)
        {
            return Reject("malformed_payload", ex.GetType());
        }

        using (payload)
        {
            return ValidateShape(payload.Root, trustedIssuer);
        }
    }

    private static bool TryCreateValidationParameters(
        OpenIdConnectOptions options,
        OpenIdConnectConfiguration? configuration,
        out string rejection,
        out string trustedIssuer,
        out TokenValidationParameters parameters)
    {
        trustedIssuer = configuration?.Issuer ?? "";
        rejection = "metadata_unavailable";
        parameters = null!;

        var signingKeys = configuration?.SigningKeys;
        if (string.IsNullOrWhiteSpace(trustedIssuer) ||
            signingKeys is null ||
            signingKeys.Count == 0 ||
            string.IsNullOrWhiteSpace(options.ClientId) ||
            configuration is null)
        {
            return false;
        }

        var allowedAlgorithms = ResolveAllowedSigningAlgorithms(configuration);
        if (allowedAlgorithms.Length == 0)
        {
            rejection = "unsupported_signing_algorithms";
            return false;
        }

        parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = trustedIssuer,
            ValidateAudience = true,
            ValidAudience = options.ClientId,
            IssuerSigningKeys = signingKeys,
            ValidAlgorithms = allowedAlgorithms,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = _clockSkew
        };

        return true;
    }

    private static string[] ResolveAllowedSigningAlgorithms(OpenIdConnectConfiguration configuration)
    {
        var advertised = configuration.IdTokenSigningAlgValuesSupported;
        if (advertised is null || advertised.Count == 0)
        {
            return _allowedSigningAlgorithms;
        }

        return advertised.Where(alg => _allowedSigningAlgorithms.Contains(alg)).ToArray();
    }

    private static bool IsAllowedAlgorithm(string? algorithm, TokenValidationParameters parameters)
    {
        return !string.IsNullOrEmpty(algorithm) &&
            parameters.ValidAlgorithms is { } allowed &&
            allowed.Contains(algorithm, StringComparer.Ordinal);
    }

    private static bool TryParseAllowedToken(
        string logoutToken,
        TokenValidationParameters parameters,
        out JsonWebToken parsedToken,
        out string? rejection,
        out Type? errorType)
    {
        try
        {
            parsedToken = new JsonWebToken(logoutToken);
        }
        catch (Exception ex)
        {
            parsedToken = null!;
            rejection = "malformed_token";
            errorType = ex.GetType();
            return false;
        }

        errorType = null;
        if (!IsAllowedAlgorithm(parsedToken.Alg, parameters))
        {
            rejection = "disallowed_algorithm";
            return false;
        }

        rejection = null;
        return true;
    }

    private async Task<(JsonWebToken? Token, Exception? Error)> ValidateSignatureAsync(
        string logoutToken,
        TokenValidationParameters parameters)
    {
        TokenValidationResult result;
        try
        {
            result = await _tokenHandler.ValidateTokenAsync(logoutToken, parameters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }

        if (result.IsValid && result.SecurityToken is JsonWebToken token)
        {
            return (token, null);
        }

        return (null, result.Exception ?? new SecurityTokenValidationException("Token validation failed."));
    }

    private async Task<OpenIdConnectConfiguration?> RefreshMetadataAsync(
        OpenIdConnectOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ConfigurationManager is not { } manager)
        {
            return null;
        }

        // Refresh at most once a minute so a forged kid cannot turn every POST
        // into a metadata fetch against the provider.
        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var last = Interlocked.Read(ref _lastMetadataRefreshUnixSeconds);
        if (now - last < (long)_metadataRefreshInterval.TotalSeconds ||
            Interlocked.CompareExchange(ref _lastMetadataRefreshUnixSeconds, now, last) != last)
        {
            return null;
        }

        manager.RequestRefresh();
        return await ResolveMetadataAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private BackChannelLogoutValidationResult ValidateShape(JsonElement payload, string trustedIssuer)
    {
        // Nested duplicates (e.g. two events members) would otherwise be
        // resolved by property enumeration order, so the signed content is
        // ambiguous.
        if (BackChannelLogoutJson.ContainsDuplicateProperties(payload))
        {
            return Reject("malformed_payload");
        }

        if (payload.TryGetProperty("nonce", out _))
        {
            return Reject("nonce_present");
        }

        if (!BackChannelLogoutJson.TryGetNonEmptyString(payload, AuthClaimTypes.Issuer, out var issuer) ||
            !string.Equals(issuer, trustedIssuer, StringComparison.Ordinal))
        {
            return Reject("invalid_issuer");
        }

        if (!BackChannelLogoutJson.TryGetInt64(payload, "iat", out var issuedAt))
        {
            return Reject("missing_iat");
        }

        if (!BackChannelLogoutJson.TryGetInt64(payload, "exp", out var expiresAt))
        {
            return Reject("missing_exp");
        }

        if (!BackChannelLogoutJson.TryGetNonEmptyString(payload, "jti", out var tokenId))
        {
            return Reject("missing_jti");
        }

        if (!BackChannelLogoutJson.HasBackChannelLogoutEvent(payload))
        {
            return Reject("invalid_events");
        }

        var (sessionRejection, subject, sid) = ValidateSessionClaims(payload);
        if (sessionRejection is not null)
        {
            return sessionRejection;
        }

        return ValidateFreshness(issuedAt, expiresAt, issuer, tokenId, subject, sid);
    }

    private (BackChannelLogoutValidationResult? Rejection, string? Subject, string? Sid) ValidateSessionClaims(
        JsonElement payload)
    {
        var subjectState = BackChannelLogoutJson.ReadOptionalStringClaim(payload, AuthClaimTypes.Subject, out var subject);
        if (subjectState == OptionalClaimState.Invalid)
        {
            return (Reject("invalid_subject"), null, null);
        }

        var sidState = BackChannelLogoutJson.ReadOptionalStringClaim(payload, AuthClaimTypes.Sid, out var sid);
        if (sidState == OptionalClaimState.Invalid)
        {
            return (Reject("invalid_sid"), null, null);
        }

        if (subjectState == OptionalClaimState.Absent && sidState == OptionalClaimState.Absent)
        {
            return (Reject("missing_session_claims"), null, null);
        }

        return (
            null,
            subjectState == OptionalClaimState.Valid ? subject : null,
            sidState == OptionalClaimState.Valid ? sid : null);
    }

    private BackChannelLogoutValidationResult ValidateFreshness(
        long issuedAt,
        long expiresAt,
        string issuer,
        string tokenId,
        string? subject,
        string? sid)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        if (issuedAt > now + (long)_clockSkew.TotalSeconds)
        {
            return Reject("invalid_iat");
        }

        // Stricter than the signature layer's skew: replay entries are kept
        // until exp.
        if (expiresAt <= now)
        {
            return Reject("token_expired");
        }

        if (expiresAt <= issuedAt)
        {
            return Reject("invalid_lifetime");
        }

        if (now - issuedAt > (long)_maxTokenAge.TotalSeconds)
        {
            return Reject("token_too_old");
        }

        if (expiresAt - issuedAt > (long)_maxTokenLifetime.TotalSeconds)
        {
            return Reject("token_lifetime_too_long");
        }

        return BackChannelLogoutValidationResult.Valid(
            issuer, subject, sid, tokenId, DateTimeOffset.FromUnixTimeSeconds(expiresAt));
    }

    private async Task<OpenIdConnectConfiguration?> ResolveMetadataAsync(
        OpenIdConnectOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.ConfigurationManager is not null)
            {
                return await options.ConfigurationManager.GetConfigurationAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return options.Configuration;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OIDC metadata unavailable for back-channel logout");
            return null;
        }
    }

    private BackChannelLogoutValidationResult Reject(string reason, Type? errorType = null)
    {
        if (errorType is null)
        {
            _logger.LogDebug("Back-channel logout rejected: {Reason}", reason);
        }
        else
        {
            _logger.LogDebug("Back-channel logout rejected: {Reason} ({ErrorType})", reason, errorType.FullName);
        }

        return BackChannelLogoutValidationResult.Invalid(reason);
    }

    private static string Classify(Exception? exception)
    {
        return exception switch
        {
            SecurityTokenExpiredException => "token_expired",
            SecurityTokenNotYetValidException => "token_not_yet_valid",
            SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException => "invalid_signature",
            SecurityTokenInvalidIssuerException => "invalid_issuer",
            SecurityTokenInvalidAudienceException => "invalid_audience",
            SecurityTokenNoExpirationException or SecurityTokenInvalidLifetimeException => "invalid_lifetime",
            SecurityTokenInvalidAlgorithmException => "disallowed_algorithm",
            SecurityTokenMalformedException or SecurityTokenException => "invalid_token",
            ArgumentException => "malformed_token",
            _ => "invalid_token"
        };
    }
}
