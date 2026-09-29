using Microsoft.Extensions.Configuration;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class AuthenticationOptionsValidatorTests
{
    private static AuthenticationOptionsValidator CreateValidator(string? allowedHosts = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AllowedHosts"] = allowedHosts
            })
            .Build();
        return new AuthenticationOptionsValidator(config);
    }

    private static AuthenticationOptions ValidOidcOptions() => new()
    {
        Mode = "OIDC",
        Oidc = new OidcOptions
        {
            Authority = "https://idp.example.com",
            ClientId = "mqttprobe",
            ClientSecret = "secret",
            ProviderDisplayName = "My IdP",
            AdmissionClaim = "groups",
            AcceptedValues = ["mqttprobe-users"],
            PublicBaseUrl = "https://mqtt.example.com"
        }
    };

    // ── Local mode ───────────────────────────────────────────────────────────

    [Test]
    public void Validate_LocalMode_NoOidcConfig_ReturnsSuccess()
    {
        var validator = CreateValidator();
        var options = new AuthenticationOptions { Mode = "Local" };

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    // ── Valid OIDC ───────────────────────────────────────────────────────────

    [Test]
    public void Validate_OidcMode_AllFieldsValid_ReturnsSuccess()
    {
        var validator = CreateValidator();

        var result = validator.Validate(null, ValidOidcOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_ConfiguredAllowedHosts_ReturnsSuccess()
    {
        var validator = CreateValidator("mqtt.example.com");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Validate_OidcMode_WithPublicBaseUrl_WildcardAllowedHosts_ReturnsSuccess()
    {
        var validator = CreateValidator("*");
        var options = ValidOidcOptions();

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Validate_AuthorityWithTrailingSlash_ReturnsSuccess()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = "https://idp.example.com/";

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Validate_PublicBaseUrlWithPort_ReturnsSuccess()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = "https://mqtt.example.com:8443";

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    // ── Case-insensitive mode ────────────────────────────────────────────────

    [TestCase("oidc")]
    [TestCase("OIDC")]
    [TestCase("Oidc")]
    [TestCase("OidC")]
    public void Validate_OidcModeIsCaseInsensitive(string mode)
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Mode = mode;

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [TestCase("local")]
    [TestCase("LOCAL")]
    [TestCase("Local")]
    public void Validate_LocalModeIsCaseInsensitive(string mode)
    {
        var validator = CreateValidator();
        var options = new AuthenticationOptions { Mode = mode };

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Validate_UnknownMode_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = new AuthenticationOptions { Mode = "SAML" };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("SAML"));
    }

    // ── Missing required OIDC fields ─────────────────────────────────────────

    [Test]
    public void Validate_OidcMode_MissingAuthority_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("Authority"));
    }

    [Test]
    public void Validate_OidcMode_MissingClientId_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.ClientId = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ClientId"));
    }

    [Test]
    public void Validate_OidcMode_MissingClientSecret_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.ClientSecret = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ClientSecret"));
    }

    [Test]
    public void Validate_OidcMode_MissingProviderDisplayName_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.ProviderDisplayName = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ProviderDisplayName"));
    }

    [Test]
    public void Validate_OidcMode_MissingAdmissionClaim_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.AdmissionClaim = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AdmissionClaim"));
    }

    // ── AcceptedValues edge cases ────────────────────────────────────────────

    [Test]
    public void Validate_OidcMode_EmptyAcceptedValues_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.AcceptedValues = [];

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AcceptedValue"));
    }

    [Test]
    public void Validate_OidcMode_BlankAcceptedValue_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.AcceptedValues = ["valid", ""];

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("blank"));
    }

    [Test]
    public void Validate_OidcMode_WhitespaceAcceptedValue_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.AcceptedValues = ["valid", "  "];

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("blank"));
    }

    [Test]
    public void Validate_OidcMode_SingleAcceptedValue_ReturnsSuccess()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.AcceptedValues = ["only-one"];

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void Validate_OidcMode_MultipleAcceptedValues_ReturnsSuccess()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.AcceptedValues = ["group-a", "group-b", "group-c"];

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    // ── Authority URI validation ─────────────────────────────────────────────

    [TestCase("http://idp.example.com")] // DevSkim: ignore DS137138 HTTPS-only validator negative case
    [TestCase("http://idp.example.com:8080")] // DevSkim: ignore DS137138 HTTPS-only validator negative case
    public void Validate_AuthorityRequiresHttps(string authority)
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = authority;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("HTTPS"));
    }

    [Test]
    public void Validate_AuthorityNotAbsolute_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = "not-a-url";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("absolute"));
    }

    [Test]
    public void Validate_AuthorityWithUserinfo_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = "https://user:pass@idp.example.com";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("userinfo"));
    }

    [Test]
    public void Validate_AuthorityWithQuery_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = "https://idp.example.com?realm=test";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("query"));
    }

    [Test]
    public void Validate_AuthorityWithFragment_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = "https://idp.example.com#fragment";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("fragment"));
    }

    // Authority allows paths (Keycloak/Authentik)
    [TestCase("https://idp.example.com/realms/test")]
    [TestCase("https://idp.example.com/oidc")]
    public void Validate_AuthorityWithPath_ReturnsSuccess(string authority)
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.Authority = authority;

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    // ── PublicBaseUrl URI validation ─────────────────────────────────────────

    [TestCase("http://mqtt.example.com")] // DevSkim: ignore DS137138 HTTPS-only validator negative case
    public void Validate_PublicBaseUrlRequiresHttps(string publicBaseUrl)
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = publicBaseUrl;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("HTTPS"));
    }

    [Test]
    public void Validate_PublicBaseUrlNotAbsolute_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = "not-a-url";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("absolute"));
    }

    [Test]
    public void Validate_PublicBaseUrlWithUserinfo_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = "https://user:pass@mqtt.example.com";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("userinfo"));
    }

    [Test]
    public void Validate_PublicBaseUrlWithQuery_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = "https://mqtt.example.com?q=1";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("query"));
    }

    [Test]
    public void Validate_PublicBaseUrlWithFragment_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = "https://mqtt.example.com#frag";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("fragment"));
    }

    // PublicBaseUrl remains origin-only (no path)
    [Test]
    public void Validate_PublicBaseUrlWithNonRootPath_ReturnsFailure()
    {
        var validator = CreateValidator();
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = "https://mqtt.example.com/app";

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("path"));
    }

    // ── AllowedHosts constraint ──────────────────────────────────────────────

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_WildcardAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("*");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_EmptyAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_NullAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator(null);
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_MultiHostAllowedHosts_ReturnsSuccess()
    {
        var validator = CreateValidator("mqtt.example.com;backup.example.com");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    // ── AllowedHosts wildcard token parsing ──────────────────────────────────

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_WildcardTokenInAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("mqtt.example.com;*;backup.example.com");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_EmptyTokenInAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("mqtt.example.com;;backup.example.com");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    // ── AllowedHosts wildcard bind addresses ─────────────────────────────────

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_StarAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("*");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_Ipv4WildcardAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("0.0.0.0");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_Ipv6WildcardAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("::");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    [Test]
    public void Validate_OidcMode_NoPublicBaseUrl_Ipv6BracketedWildcardAllowedHosts_ReturnsFailure()
    {
        var validator = CreateValidator("[::]");
        var options = ValidOidcOptions();
        options.Oidc.PublicBaseUrl = null;

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }

    // ── Multiple errors ──────────────────────────────────────────────────────

    [Test]
    public void Validate_OidcMode_MultipleMissingFields_ReturnsAllErrors()
    {
        var validator = CreateValidator();
        var options = new AuthenticationOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions()
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("Authority"));
        result.Failures.Should().Contain(f => f.Contains("ClientId"));
        result.Failures.Should().Contain(f => f.Contains("ClientSecret"));
        result.Failures.Should().Contain(f => f.Contains("ProviderDisplayName"));
        result.Failures.Should().Contain(f => f.Contains("AdmissionClaim"));
        result.Failures.Should().Contain(f => f.Contains("AcceptedValue"));
        result.Failures.Should().Contain(f => f.Contains("AllowedHosts"));
    }
}
