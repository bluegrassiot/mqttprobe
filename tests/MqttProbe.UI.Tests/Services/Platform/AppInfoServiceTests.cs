using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Web.Authentication;
using MqttProbe.Web.Services;

namespace MqttProbe.UI.Tests.Services.Platform;

[TestFixture]
public class AppInfoServiceTests
{
    private static IOptions<AuthenticationOptions> LocalOptions() =>
        Options.Create(new AuthenticationOptions { Mode = "Local" });

    private static IOptions<AuthenticationOptions> OidcOptions(string providerDisplayName = "Keycloak") =>
        Options.Create(new AuthenticationOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions { ProviderDisplayName = providerDisplayName }
        });

    [Test]
    public void GetVersion_ReturnsNonNullNonEmptyString()
    {
        var service = new AppInfoService(LocalOptions(), () => "1.0.0", () => null);

        var version = service.GetVersion();
        version.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void GetVersion_DoesNotContainPlusSign()
    {
        var service = new AppInfoService(LocalOptions(), () => "1.0.0+build", () => null);

        var version = service.GetVersion();
        version.Should().NotContain("+");
    }

    [Test]
    public void GetVersion_WhenVersionSourcesUnavailable_ReturnsUnknown()
    {
        var service = new AppInfoService(LocalOptions(), () => null, () => null);

        service.GetVersion().Should().Be("unknown");
    }

    [Test]
    public void GetVersion_WhenAssemblyVersionLookupThrows_FallsBackToProcessVersion()
    {
        var service = new AppInfoService(
            LocalOptions(),
            () => throw new InvalidOperationException("assembly metadata unavailable"),
            () => "1.2.3+build");

        service.GetVersion().Should().Be("1.2.3");
    }

    [Test]
    public void GetVersion_WhenBothProvidersReturnValues_PrefersAssemblyInformationalVersion()
    {
        var service = new AppInfoService(
            LocalOptions(),
            () => "1.2.3+build",
            () => "9.9.9");

        service.GetVersion().Should().Be("1.2.3");
    }

    [Test]
    public void GetVersion_WhenAllVersionLookupsThrow_ReturnsUnknown()
    {
        var service = new AppInfoService(
            LocalOptions(),
            () => throw new InvalidOperationException("assembly metadata unavailable"),
            () => throw new InvalidOperationException("process metadata unavailable"));

        service.GetVersion().Should().Be("unknown");
    }

    [Test]
    public void RequiresAuthentication_ReturnsTrue()
    {
        var service = new AppInfoService(LocalOptions(), () => null, () => null);

        service.RequiresAuthentication.Should().BeTrue();
    }

    [Test]
    public void IsOidcMode_Local_ReturnsFalse()
    {
        var service = new AppInfoService(LocalOptions(), () => null, () => null);

        service.IsOidcMode.Should().BeFalse();
    }

    [Test]
    public void ProviderDisplayName_Local_ReturnsNull()
    {
        var service = new AppInfoService(LocalOptions(), () => null, () => null);

        service.ProviderDisplayName.Should().BeNull();
    }

    [Test]
    public void IsOidcMode_Oidc_ReturnsTrue()
    {
        var service = new AppInfoService(OidcOptions(), () => null, () => null);

        service.IsOidcMode.Should().BeTrue();
    }

    [Test]
    public void ProviderDisplayName_Oidc_ReturnsConfiguredName()
    {
        var service = new AppInfoService(OidcOptions("Authentik"), () => null, () => null);

        service.ProviderDisplayName.Should().Be("Authentik");
    }

    [Test]
    public void IsOidcMode_CaseInsensitiveMatch_ReturnsTrue()
    {
        var options = Options.Create(new AuthenticationOptions
        {
            Mode = "oidc",
            Oidc = new OidcOptions { ProviderDisplayName = "Authentik" }
        });

        var service = new AppInfoService(options, () => null, () => null);

        service.IsOidcMode.Should().BeTrue();
    }

    [Test]
    public void ProviderDisplayName_Oidc_NullDisplayName_ReturnsNull()
    {
        var options = Options.Create(new AuthenticationOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions { ProviderDisplayName = null }
        });

        var service = new AppInfoService(options, () => null, () => null);

        service.ProviderDisplayName.Should().BeNull();
    }

    [Test]
    public void IsOidcMode_DefaultOptions_ReturnsFalse()
    {
        var service = new AppInfoService(
            Options.Create(new AuthenticationOptions()),
            () => null,
            () => null);

        service.IsOidcMode.Should().BeFalse();
    }

    // --- ServiceCollection resolution tests ---

    [Test]
    public void ServiceCollection_LocalMode_ResolvesIsOidcModeFalse()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<AuthenticationOptions>(opt => opt.Mode = "Local");
        services.AddSingleton<IAppInfoService, AppInfoService>();
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IAppInfoService>();

        service.IsOidcMode.Should().BeFalse();
        service.ProviderDisplayName.Should().BeNull();
        service.RequiresAuthentication.Should().BeTrue();
    }

    [Test]
    public void ServiceCollection_OidcMode_ResolvesIsOidcModeTrue()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<AuthenticationOptions>(opt =>
        {
            opt.Mode = "OIDC";
            opt.Oidc = new OidcOptions { ProviderDisplayName = "Keycloak" };
        });
        services.AddSingleton<IAppInfoService, AppInfoService>();
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IAppInfoService>();

        service.IsOidcMode.Should().BeTrue();
        service.ProviderDisplayName.Should().Be("Keycloak");
    }

    [Test]
    public void ServiceCollection_DefaultOptions_ResolvesLocalSafe()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        // No Configure call — options stay at defaults (Mode = "Local")
        services.AddSingleton<IAppInfoService, AppInfoService>();
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IAppInfoService>();

        service.IsOidcMode.Should().BeFalse();
        service.ProviderDisplayName.Should().BeNull();
    }

    [Test]
    public void ServiceCollection_OidcProviderName_PassesThrough()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<AuthenticationOptions>(opt =>
        {
            opt.Mode = "OIDC";
            opt.Oidc = new OidcOptions { ProviderDisplayName = "Authentik" };
        });
        services.AddSingleton<IAppInfoService, AppInfoService>();
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IAppInfoService>();

        service.IsOidcMode.Should().BeTrue();
        service.ProviderDisplayName.Should().Be("Authentik");
    }
}
