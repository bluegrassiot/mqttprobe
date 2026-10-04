using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class AuthenticationConfiguratorTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static AuthenticationConfigurator CreateConfigurator(
        AuthenticationOptions? options = null)
    {
        var tp = new FakeTimeProvider(_epoch);
        var coordinator = new AppSessionCoordinator(tp, TimeSpan.FromHours(8));
        var denialStore = new DenialStateStore(tp);
        var logger = Substitute.For<ILogger<OidcAuthenticationEvents>>();
        var authOptions = options ?? new AuthenticationOptions();
        var events = new OidcAuthenticationEvents(coordinator, denialStore, Options.Create(authOptions), logger);
        return new AuthenticationConfigurator(Options.Create(authOptions), events);
    }

    // ── Cookie PostConfigure ─────────────────────────────────────────────────

    [Test]
    public void PostConfigureCookie_OidcMode_WiresValidatePrincipalEvent()
    {
        var options = new AuthenticationOptions { Mode = "OIDC" };
        var configurator = CreateConfigurator(options);
        var cookieOptions = new CookieAuthenticationOptions();

        configurator.PostConfigure(null, cookieOptions);

        cookieOptions.Events.OnValidatePrincipal.Should().NotBeNull();
    }

    [Test]
    public void PostConfigureCookie_LocalMode_DoesNotWireEvents()
    {
        var options = new AuthenticationOptions { Mode = "Local" };
        var configurator = CreateConfigurator(options);
        var cookieOptions = new CookieAuthenticationOptions();
        var defaultHandler = cookieOptions.Events.OnValidatePrincipal;

        configurator.PostConfigure(null, cookieOptions);

        cookieOptions.Events.OnValidatePrincipal.Should().BeSameAs(defaultHandler);
    }

    // ── OIDC PostConfigure ───────────────────────────────────────────────────

    [Test]
    public void PostConfigureOidc_OidcMode_WiresTokenValidatedEvent()
    {
        var options = new AuthenticationOptions { Mode = "OIDC" };
        var configurator = CreateConfigurator(options);
        var oidcOptions = new OpenIdConnectOptions();

        configurator.PostConfigure(null, oidcOptions);

        oidcOptions.Events.OnTokenValidated.Should().NotBeNull();
    }

    [Test]
    public void PostConfigureOidc_OidcMode_WiresRemoteFailureEvent()
    {
        var options = new AuthenticationOptions { Mode = "OIDC" };
        var configurator = CreateConfigurator(options);
        var oidcOptions = new OpenIdConnectOptions();

        configurator.PostConfigure(null, oidcOptions);

        oidcOptions.Events.OnRemoteFailure.Should().NotBeNull();
    }

    [Test]
    public void PostConfigureOidc_OidcMode_WiresRedirectEvents()
    {
        var options = new AuthenticationOptions { Mode = "OIDC" };
        var configurator = CreateConfigurator(options);
        var oidcOptions = new OpenIdConnectOptions();

        configurator.PostConfigure(null, oidcOptions);

        oidcOptions.Events.OnRedirectToIdentityProvider.Should().NotBeNull();
        oidcOptions.Events.OnRedirectToIdentityProviderForSignOut.Should().NotBeNull();
    }

    [Test]
    public void PostConfigureOidc_LocalMode_DoesNotWireEvents()
    {
        var options = new AuthenticationOptions { Mode = "Local" };
        var configurator = CreateConfigurator(options);
        var oidcOptions = new OpenIdConnectOptions();
        var defaultTokenHandler = oidcOptions.Events.OnTokenValidated;
        var defaultFailureHandler = oidcOptions.Events.OnRemoteFailure;

        configurator.PostConfigure(null, oidcOptions);

        oidcOptions.Events.OnTokenValidated.Should().BeSameAs(defaultTokenHandler);
        oidcOptions.Events.OnRemoteFailure.Should().BeSameAs(defaultFailureHandler);
    }
}
