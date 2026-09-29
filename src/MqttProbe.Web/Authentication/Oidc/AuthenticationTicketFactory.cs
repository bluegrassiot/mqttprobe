using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Web.Authentication;

public static class AuthenticationTicketFactory
{
    public static AuthenticationTicket Create(
        ValidatedIdTokenPayload payload,
        string sessionId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string idToken)
    {
        return Create(payload.Root, sessionId, issuedAt, expiresAt, idToken);
    }

    public static AuthenticationTicket Create(
        JsonElement payload,
        string sessionId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string idToken)
    {
        var principal = MinimalPrincipalFactory.Create(payload, sessionId, expiresAt);

        var properties = new AuthenticationProperties
        {
            IssuedUtc = issuedAt,
            ExpiresUtc = expiresAt
        };

        // Store only the id_token for RP-initiated logout hint
        properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = idToken }]);

        return new AuthenticationTicket(principal, properties, "OIDC");
    }
}
