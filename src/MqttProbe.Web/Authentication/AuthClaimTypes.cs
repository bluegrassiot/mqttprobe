namespace MqttProbe.Web.Authentication;

public static class AuthClaimTypes
{
    // ID-token claim names (case-sensitive, as they appear in the token payload)
    public const string Issuer = "iss";
    public const string Subject = "sub";
    public const string Name = "name";
    public const string PreferredUsername = "preferred_username";

    // Provider session id from the ID token, distinct from AppSessionId.
    public const string Sid = "sid";

    // App principal claim types (explicit, not remapped)
    public const string AppIssuer = "iss";
    public const string AppSubject = "sub";
    public const string AppDisplayName = "display_name";
    public const string AppSessionId = "session_id";
    public const string AppSessionExpiry = "session_expiry";
    public const string AppRole = "role";
}
