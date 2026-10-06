namespace MqttProbe.Web.Authentication;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string Mode { get; set; } = "Local";

    public OidcOptions Oidc { get; set; } = new();
}
