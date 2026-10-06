using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MqttProbe.Web.Authentication;

namespace MqttProbe.IntegrationTests.Authentication;

internal sealed class LoopbackOidcOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    private readonly string _allowedAuthority;
    private readonly AuthenticationOptionsValidator _inner;

    public LoopbackOidcOptionsValidator(IConfiguration configuration, string allowedAuthority)
    {
        if (!Uri.TryCreate(allowedAuthority, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback)
        {
            throw new ArgumentException(
                "allowedAuthority must be an absolute HTTP URI targeting a loopback address.",
                nameof(allowedAuthority));
        }

        _allowedAuthority = allowedAuthority;
        _inner = new AuthenticationOptionsValidator(configuration);
    }

    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        if (!string.Equals(options.Oidc?.Authority, _allowedAuthority, StringComparison.Ordinal))
        {
            return _inner.Validate(name, options);
        }

        var oidc = options.Oidc!;
        var httpsAuthority = new UriBuilder(new Uri(oidc.Authority!))
        {
            Scheme = Uri.UriSchemeHttps
        }.Uri.ToString();

        var cloned = new AuthenticationOptions
        {
            Mode = options.Mode,
            Oidc = new OidcOptions
            {
                Authority = httpsAuthority,
                ClientId = oidc.ClientId,
                ClientSecret = oidc.ClientSecret,
                ProviderDisplayName = oidc.ProviderDisplayName,
                AdmissionClaim = oidc.AdmissionClaim,
                AcceptedValues = oidc.AcceptedValues,
                PublicBaseUrl = oidc.PublicBaseUrl
            }
        };

        return _inner.Validate(name, cloned);
    }
}
