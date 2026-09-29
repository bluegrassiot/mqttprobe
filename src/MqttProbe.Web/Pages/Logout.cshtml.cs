using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MqttProbe.Web.Authentication;
using AuthOptions = MqttProbe.Web.Authentication.AuthenticationOptions;

namespace MqttProbe.Pages;

[AllowAnonymous]
[ValidateAntiForgeryToken]
public class LogoutModel : PageModel
{
    // Query-value category surfaced on /Login when provider sign-out cannot proceed.
    public const string SignOutIncompleteError = "signout_incomplete";

    private readonly AuthOptions _authOptions;
    private readonly AppSessionCoordinator? _coordinator;
    private readonly IOptionsMonitor<OpenIdConnectOptions>? _oidcOptionsMonitor;
    private readonly ILogger<LogoutModel> _logger;

    public LogoutModel(
        IOptions<AuthOptions> authOptions,
        AppSessionCoordinator? coordinator = null,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        ILogger<LogoutModel>? logger = null)
    {
        _authOptions = authOptions.Value;
        _coordinator = coordinator;
        _oidcOptionsMonitor = oidcOptionsMonitor;
        _logger = logger ?? NullLogger<LogoutModel>.Instance;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (_authOptions.Mode.Equals("OIDC", StringComparison.OrdinalIgnoreCase))
        {
            // OIDC mode: read app session + retained id_token
            var sessionId = HttpContext.User.FindFirst(AuthClaimTypes.AppSessionId)?.Value;
            var idToken = await HttpContext.GetTokenAsync("id_token");

            // Await coordinator cleanup first. Force-login navigation is
            // suppressed: the browser is about to follow the identity provider
            // redirect, and a competing /Login navigation would race it.
            if (!string.IsNullOrEmpty(sessionId) && _coordinator is not null)
            {
                await _coordinator.RevokeSessionAsync(sessionId, notifyForceLogin: false);
            }

            // Clear local cookie before any provider call so an unreachable
            // identity provider can never leave an authenticated browser session.
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            var properties = new AuthenticationProperties { RedirectUri = "/Login" };
            if (!string.IsNullOrEmpty(idToken))
            {
                properties.StoreTokens([
                    new AuthenticationToken { Name = "id_token", Value = idToken }
                ]);
            }

            var blocker = await GetSignOutBlockerAsync();
            if (blocker is not null)
            {
                _logger.LogWarning(
                    "OIDC sign-out incomplete ({Reason}); local session was cleared but the identity provider session may still be active",
                    blocker.Reason);
                if (blocker.Exception is not null)
                {
                    _logger.LogWarning(blocker.Exception, "OIDC sign-out metadata retrieval failed");
                }

                // Loud, user-visible outcome instead of a silent local-only logout:
                // /Login renders SignOutIncompleteError as a message.
                return RedirectToPage("/Login", new { error = SignOutIncompleteError });
            }

            // Discovery confirmed an end_session_endpoint: hand off to the OIDC
            // handler, which redirects there carrying id_token_hint and the public
            // post-logout callback (both attached by OidcAuthenticationEvents).
            return SignOut(properties, OpenIdConnectDefaults.AuthenticationScheme);
        }

        // Local mode: unchanged
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }

    // Returns the reason remote sign-out cannot proceed, or null when it can.
    // Uses the same cached ConfigurationManager instance the OIDC handler will
    // use, so "available here" means "available to the handler".
    private async Task<SignOutBlocker?> GetSignOutBlockerAsync()
    {
        if (_oidcOptionsMonitor is null)
        {
            return new SignOutBlocker("oidc_options_unavailable");
        }

        var configManager = _oidcOptionsMonitor
            .Get(OpenIdConnectDefaults.AuthenticationScheme)
            .ConfigurationManager;
        if (configManager is null)
        {
            return new SignOutBlocker("configuration_manager_unavailable");
        }

        OpenIdConnectConfiguration config;
        try
        {
            config = await configManager.GetConfigurationAsync(HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SignOutBlocker("metadata_unavailable", ex);
        }

        return string.IsNullOrEmpty(config.EndSessionEndpoint)
            ? new SignOutBlocker("end_session_endpoint_missing")
            : null;
    }

    private sealed record SignOutBlocker(string Reason, Exception? Exception = null);
}
