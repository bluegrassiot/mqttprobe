using Microsoft.Extensions.Options;

namespace MqttProbe.Web.Authentication;

public sealed class AuthenticationOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    private readonly IConfiguration _configuration;

    public AuthenticationOptionsValidator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        if (!TryParseMode(options.Mode, out var mode))
        {
            return ValidateOptionsResult.Fail(
                $"Unknown authentication mode '{options.Mode}'. Expected 'Local' or 'OIDC'.");
        }

        if (mode == AuthenticationMode.Local)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOidcOptions(options.Oidc);
    }

    private ValidateOptionsResult ValidateOidcOptions(OidcOptions oidc)
    {
        var errors = new List<string>();

        ValidateRequiredFields(oidc, errors);
        ValidateAcceptedValues(oidc, errors);
        ValidateOrigins(oidc, errors);

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateRequiredFields(OidcOptions oidc, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(oidc.Authority))
            errors.Add("Authority is required in OIDC mode.");

        if (string.IsNullOrWhiteSpace(oidc.ClientId))
            errors.Add("ClientId is required in OIDC mode.");

        if (string.IsNullOrWhiteSpace(oidc.ClientSecret))
            errors.Add("ClientSecret is required in OIDC mode.");

        if (string.IsNullOrWhiteSpace(oidc.ProviderDisplayName))
            errors.Add("ProviderDisplayName is required in OIDC mode.");

        if (string.IsNullOrWhiteSpace(oidc.AdmissionClaim))
            errors.Add("AdmissionClaim is required in OIDC mode.");
    }

    private static void ValidateAcceptedValues(OidcOptions oidc, List<string> errors)
    {
        if (oidc.AcceptedValues.Length == 0)
            errors.Add("At least one AcceptedValue is required in OIDC mode.");
        else if (oidc.AcceptedValues.Any(string.IsNullOrWhiteSpace))
            errors.Add("AcceptedValues must not contain blank entries.");
    }

    private void ValidateOrigins(OidcOptions oidc, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(oidc.Authority) &&
            !TryValidateHttpsAbsoluteUri(oidc.Authority, "Authority", allowPath: true, out var authorityError))
        {
            errors.Add(authorityError);
        }

        if (!string.IsNullOrWhiteSpace(oidc.PublicBaseUrl))
        {
            if (!TryValidateHttpsAbsoluteUri(oidc.PublicBaseUrl, "PublicBaseUrl", allowPath: false, out var publicBaseUrlError))
                errors.Add(publicBaseUrlError);
        }
        else
        {
            var allowedHosts = _configuration.GetValue<string>("AllowedHosts");
            if (!HasRestrictiveAllowedHosts(allowedHosts))
            {
                errors.Add(
                    "AllowedHosts must be configured (not wildcard) when PublicBaseUrl is not set in OIDC mode.");
            }
        }
    }

    internal static bool HasRestrictiveAllowedHosts(string? allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(allowedHosts))
            return false;

        var tokens = allowedHosts.Split(';', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        return !tokens.Any(token => IsDisallowedHostToken(token));
    }

    private static bool IsDisallowedHostToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return true;

        if (string.Equals(token, "*", StringComparison.Ordinal))
            return true;

        if (string.Equals(token, "0.0.0.0", StringComparison.Ordinal))
            return true;

        if (string.Equals(token, "[::]", StringComparison.Ordinal) ||
            string.Equals(token, "::", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool TryParseMode(string value, out AuthenticationMode mode)
    {
        if (string.Equals(value, "Local", StringComparison.OrdinalIgnoreCase))
        {
            mode = AuthenticationMode.Local;
            return true;
        }

        if (string.Equals(value, "OIDC", StringComparison.OrdinalIgnoreCase))
        {
            mode = AuthenticationMode.Oidc;
            return true;
        }

        mode = default;
        return false;
    }

    // Authority is an HTTPS absolute URI and may contain a path (Keycloak/Authentik).
    // PublicBaseUrl is HTTPS origin-only (no path).
    private static bool TryValidateHttpsAbsoluteUri(string value, string fieldName, bool allowPath, out string error)
    {
        error = "";

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = $"{fieldName} '{value}' is not a valid absolute URI.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{fieldName} '{value}' must use HTTPS.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = $"{fieldName} '{value}' must not contain userinfo.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            error = $"{fieldName} '{value}' must not contain a query string.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = $"{fieldName} '{value}' must not contain a fragment.";
            return false;
        }

        if (!allowPath && uri.AbsolutePath != "/")
        {
            error = $"{fieldName} '{value}' must not contain a path (subpath hosting is deferred).";
            return false;
        }

        return true;
    }
}
