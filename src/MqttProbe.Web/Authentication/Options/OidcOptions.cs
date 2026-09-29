namespace MqttProbe.Web.Authentication;

public sealed class OidcOptions
{
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? ProviderDisplayName { get; set; }

    public string? AdmissionClaim { get; set; }

    public string[] AcceptedValues { get; set; } = [];

    public string? PublicBaseUrl { get; set; }
}
