using Microsoft.AspNetCore.Http;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class PublicOriginResolverTests
{
    private static HttpRequest CreateRequest(string scheme = "https", string host = "mqtt.example.com")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }

    // ── Explicit PublicBaseUrl ───────────────────────────────────────────────

    [Test]
    public void ResolveOrigin_ExplicitPublicBaseUrl_ReturnsThatOrigin()
    {
        var resolver = new PublicOriginResolver("https://mqtt.example.com", "*");
        var request = CreateRequest();

        var origin = resolver.ResolveOrigin(request);

        origin.Should().Be(new Uri("https://mqtt.example.com"));
    }

    [Test]
    public void ResolveOrigin_ExplicitPublicBaseUrl_PreservesPort()
    {
        var resolver = new PublicOriginResolver("https://mqtt.example.com:8443", "*");
        var request = CreateRequest();

        var origin = resolver.ResolveOrigin(request);

        origin.Should().Be(new Uri("https://mqtt.example.com:8443"));
    }

    [Test]
    public void ResolveOrigin_ExplicitPublicBaseUrl_StripsPath()
    {
        var resolver = new PublicOriginResolver("https://mqtt.example.com/", "*");
        var request = CreateRequest();

        var origin = resolver.ResolveOrigin(request);

        origin.AbsolutePath.Should().Be("/");
    }

    [Test]
    public void ResolveOrigin_ExplicitPublicBaseUrl_IgnoresRequestHost()
    {
        var resolver = new PublicOriginResolver("https://mqtt.example.com", "*");
        var request = CreateRequest("https", "evil.example.com");

        var origin = resolver.ResolveOrigin(request);

        origin.Host.Should().Be("mqtt.example.com");
    }

    // ── Derived from request ─────────────────────────────────────────────────

    [Test]
    public void ResolveOrigin_NoPublicBaseUrl_RestrictiveAllowedHosts_DerivesFromRequest()
    {
        var resolver = new PublicOriginResolver(null, "mqtt.example.com");
        var request = CreateRequest("https", "mqtt.example.com");

        var origin = resolver.ResolveOrigin(request);

        origin.Should().Be(new Uri("https://mqtt.example.com"));
    }

    [Test]
    public void ResolveOrigin_DerivesFromRequest_PreservesPort()
    {
        var resolver = new PublicOriginResolver(null, "mqtt.example.com");
        var request = CreateRequest("https", "mqtt.example.com:8443");

        var origin = resolver.ResolveOrigin(request);

        origin.Should().Be(new Uri("https://mqtt.example.com:8443"));
    }

    // ── HTTPS enforcement on request-derived origin ──────────────────────────

    [Test]
    public void ResolveOrigin_DerivedFromHttp_Throws()
    {
        var resolver = new PublicOriginResolver(null, "mqtt.example.com");
        var request = CreateRequest("http", "mqtt.example.com");

        var act = () => resolver.ResolveOrigin(request);

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Unsafe AllowedHosts ──────────────────────────────────────────────────

    [Test]
    public void ResolveOrigin_NoPublicBaseUrl_WildcardAllowedHosts_Throws()
    {
        var resolver = new PublicOriginResolver(null, "*");
        var request = CreateRequest();

        var act = () => resolver.ResolveOrigin(request);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ResolveOrigin_NoPublicBaseUrl_EmptyAllowedHosts_Throws()
    {
        var resolver = new PublicOriginResolver(null, "");
        var request = CreateRequest();

        var act = () => resolver.ResolveOrigin(request);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ResolveOrigin_NoPublicBaseUrl_NullAllowedHosts_Throws()
    {
        var resolver = new PublicOriginResolver(null, null);
        var request = CreateRequest();

        var act = () => resolver.ResolveOrigin(request);

        act.Should().Throw<InvalidOperationException>();
    }
}
