namespace MqttProbe.Web.Authentication;

public sealed class PublicOriginResolver
{
    private readonly Uri? _explicitOrigin;
    private readonly bool _allowedHostsIsRestrictive;

    public PublicOriginResolver(string? publicBaseUrl, string? allowedHosts)
    {
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            _explicitOrigin = new Uri(publicBaseUrl);
        }

        _allowedHostsIsRestrictive = AuthenticationOptionsValidator.HasRestrictiveAllowedHosts(allowedHosts);
    }

    public Uri ResolveOrigin(HttpRequest request)
    {
        if (_explicitOrigin is not null)
        {
            return new Uri(_explicitOrigin.GetLeftPart(UriPartial.Authority));
        }

        if (!_allowedHostsIsRestrictive)
        {
            throw new InvalidOperationException(
                "PublicBaseUrl must be set or AllowedHosts must be configured when using OIDC mode.");
        }

        var scheme = request.Scheme;
        if (!string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Request-derived origin must use HTTPS in OIDC mode.");
        }

        var host = request.Host;
        return new Uri($"{scheme}://{host}");
    }
}
