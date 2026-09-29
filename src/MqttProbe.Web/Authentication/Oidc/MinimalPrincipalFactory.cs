using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Web.Authentication;

public static class MinimalPrincipalFactory
{
    public static ClaimsPrincipal Create(
        ValidatedIdTokenPayload payload,
        string sessionId,
        DateTimeOffset sessionExpiry)
    {
        return Create(payload.Root, sessionId, sessionExpiry);
    }

    public static ClaimsPrincipal Create(
        JsonElement payload,
        string sessionId,
        DateTimeOffset sessionExpiry)
    {
        var issuer = GetStringProperty(payload, AuthClaimTypes.Issuer);
        var subject = GetStringProperty(payload, AuthClaimTypes.Subject);
        var displayName = AdmissionEvaluator.GetDisplayName(payload);

        // Set NameClaimType and RoleClaimType to app claim types
        // so Identity.Name and IsInRole work without duplicate ClaimTypes.Name/Role
        var identity = new ClaimsIdentity(
            claims: [
                new Claim(AuthClaimTypes.AppIssuer, issuer),
                new Claim(AuthClaimTypes.AppSubject, subject),
                new Claim(AuthClaimTypes.AppDisplayName, displayName),
                new Claim(AuthClaimTypes.AppSessionId, sessionId),
                new Claim(AuthClaimTypes.AppSessionExpiry, sessionExpiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
                new Claim(AuthClaimTypes.AppRole, AppRoles.Admin)
            ],
            authenticationType: "OIDC",
            nameType: AuthClaimTypes.AppDisplayName,
            roleType: AuthClaimTypes.AppRole);

        return new ClaimsPrincipal(identity);
    }

    private static string GetStringProperty(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? "";
        }

        return "";
    }
}
