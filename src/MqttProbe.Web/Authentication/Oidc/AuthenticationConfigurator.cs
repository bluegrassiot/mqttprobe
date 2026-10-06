using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace MqttProbe.Web.Authentication;

public sealed class AuthenticationConfigurator :
    IPostConfigureOptions<CookieAuthenticationOptions>,
    IPostConfigureOptions<OpenIdConnectOptions>
{
    private readonly AuthenticationOptions _options;
    private readonly OidcAuthenticationEvents _events;

    public AuthenticationConfigurator(
        IOptions<AuthenticationOptions> options,
        OidcAuthenticationEvents events)
    {
        _options = options.Value;
        _events = events;
    }

    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (_options.Mode.Equals("OIDC", StringComparison.OrdinalIgnoreCase))
        {
            options.Events.OnValidatePrincipal = _events.OnValidatePrincipal;
        }
    }

    public void PostConfigure(string? name, OpenIdConnectOptions options)
    {
        if (!_options.Mode.Equals("OIDC", StringComparison.OrdinalIgnoreCase))
            return;

        options.Events.OnTokenValidated = _events.OnTokenValidated;
        options.Events.OnRemoteFailure = OidcAuthenticationEvents.OnRemoteFailure;
        options.Events.OnRedirectToIdentityProvider = OidcAuthenticationEvents.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProviderForSignOut = _events.OnRedirectToIdentityProviderForSignOut;
    }
}
