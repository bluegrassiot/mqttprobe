namespace MqttProbe.Web.Authentication;

// Deliberately separate from the OIDC middleware's /signout-oidc, which answers
// browser-shaped requests rather than coordinator-session revocation.
public static class BackChannelLogoutEndpoint
{
    public const string Path = "/oidc/backchannel-logout";

    public static IEndpointRouteBuilder MapOidcBackChannelLogout(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, (HttpContext context, BackChannelLogoutHandler handler) =>
                handler.HandleAsync(context))
            .AllowAnonymous()
            .DisableAntiforgery();

        return endpoints;
    }
}
