using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class LocalAuthenticationHostTests
{
    [Test]
    public async Task DefaultLocalMode_GetLogin_RedirectsToSetup_WhenUnconfigured()
    {
        using var factory = new LocalModeFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Login");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Contain("/Setup");
    }

    [Test]
    public async Task DefaultLocalMode_GetLogin_ShowsLocalForm_NotOidcButton()
    {
        using var factory = new LocalModeWithAdminFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Login");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("id=\"username\"", "Local mode renders the username field");
        body.Should().Contain("id=\"password\"", "Local mode renders the password field");
        body.Should().NotContain("oidc-button", "Local mode must not render the OIDC sign-in button");
    }

    [Test]
    public async Task ExplicitLocalMode_GetLogin_RedirectsToSetup_WhenUnconfigured()
    {
        using var factory = new ExplicitLocalModeFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Login");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Contain("/Setup");
    }

    [Test]
    public async Task ExplicitLocalMode_GetLogin_ShowsLocalForm_NotOidcButton()
    {
        using var factory = new ExplicitLocalModeWithAdminFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Login");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("id=\"username\"");
        body.Should().Contain("id=\"password\"");
        body.Should().NotContain("oidc-button");
    }

    [Test]
    public async Task LocalMode_SchemeProvider_DoesNotRegisterOidcScheme()
    {
        using var factory = new LocalModeFactory();
        var schemeProvider = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        var allSchemes = await schemeProvider.GetAllSchemesAsync();
        var schemeNames = allSchemes.Select(s => s.Name).ToList();

        schemeNames.Should().NotContain(OpenIdConnectDefaults.AuthenticationScheme,
            "Local mode must not register the OpenIdConnect authentication handler");
    }

    [Test]
    public async Task LocalMode_HealthEndpoint_StillWorks()
    {
        using var factory = new LocalModeFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("OK");
    }

    [Test]
    public async Task OidcMode_SchemeProvider_RegistersOidcScheme()
    {
        using var factory = new OidcModeFactory();
        var schemeProvider = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        var scheme = await schemeProvider.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme);

        scheme.Should().NotBeNull("OIDC mode must register the OpenIdConnect authentication handler");
        scheme!.HandlerType.Should().NotBeNull();
    }

    [Test]
    public void OidcMode_OptionsMonitor_HasClientIdConfigured()
    {
        using var factory = new OidcModeFactory();
        var monitor = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>();

        var options = monitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
        options.ClientId.Should().Be("test-client-id");
    }

    [Test]
    public async Task DefaultLocalMode_AuthenticatedRoot_Returns200_WithHomeContent()
    {
        using var factory = new LocalModeWithAdminFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // GET /Login → extract antiforgery cookie + hidden token
        var loginPage = await client.GetAsync("/Login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await loginPage.Content.ReadAsStringAsync();

        var afToken = ExtractAntiForgeryToken(loginBody);
        afToken.Should().NotBeNullOrEmpty("page must contain antiforgery hidden field");

        var formData = new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = "test-password",
            ["__RequestVerificationToken"] = afToken!
        };

        // POST credentials with real cookie handler, no auto-redirect
        var postResponse = await client.PostAsync("/Login", new FormUrlEncodedContent(formData));
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResponse.Headers.Location.Should().NotBeNull();
        postResponse.Headers.Location!.OriginalString.Should().Be("/");

        // GET / with same cookie container → authenticated home page
        var rootResponse = await client.GetAsync("/");
        rootResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rootBody = await rootResponse.Content.ReadAsStringAsync();

        rootBody.Should().NotContain("id=\"username\"",
            "authenticated root must not show the login form");
    }

    [Test]
    public async Task ExplicitLocalMode_AuthenticatedRoot_Returns200_WithHomeContent()
    {
        using var factory = new ExplicitLocalModeWithAdminFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var loginPage = await client.GetAsync("/Login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await loginPage.Content.ReadAsStringAsync();

        var afToken = ExtractAntiForgeryToken(loginBody);
        afToken.Should().NotBeNullOrEmpty("page must contain antiforgery hidden field");

        var formData = new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = "test-password",
            ["__RequestVerificationToken"] = afToken!
        };

        var postResponse = await client.PostAsync("/Login", new FormUrlEncodedContent(formData));
        postResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResponse.Headers.Location.Should().NotBeNull();
        postResponse.Headers.Location!.OriginalString.Should().Be("/");

        var rootResponse = await client.GetAsync("/");
        rootResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rootBody = await rootResponse.Content.ReadAsStringAsync();

        rootBody.Should().NotContain("id=\"username\"",
            "authenticated root must not show the login form");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string? ExtractAntiForgeryToken(string html)
    {
        // Match both attribute orders: name then value, or value then name
        var m = Regex.Match(html,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(html,
            @"<input[^>]*value=""([^""]+)""[^>]*name=""__RequestVerificationToken""");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string CreateTempContentRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mqttprobe-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, "config"));
        return path;
    }

    private static void SeedAdminConfig(string contentRoot)
    {
        var hash = PasswordHasher.Hash("test-password");
        var config = new AppConfiguration
        {
            Auth = new Auth { Username = "admin", PasswordHash = hash }
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(Path.Combine(contentRoot, "config", "appsettings.json"), json);
    }

    private static void CleanupTempDir(string? path)
    {
        if (path is not null)
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }

    // ── Factories ────────────────────────────────────────────────────────

    // No Authentication config at all: code default → Local mode, no admin.
    private sealed class LocalModeFactory : WebApplicationFactory<Program>
    {
        private string? _tempContentRoot;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            _tempContentRoot = CreateTempContentRoot();

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [HostDefaults.ContentRootKey] = _tempContentRoot,
                }));

            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
                services.AddDataProtection().UseEphemeralDataProtectionProvider());
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) CleanupTempDir(_tempContentRoot);
        }
    }

    // No Authentication config, admin pre-seeded: shows login form.
    private sealed class LocalModeWithAdminFactory : WebApplicationFactory<Program>
    {
        private string? _tempContentRoot;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            _tempContentRoot = CreateTempContentRoot();
            SeedAdminConfig(_tempContentRoot);

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [HostDefaults.ContentRootKey] = _tempContentRoot,
                }));

            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
                services.AddDataProtection().UseEphemeralDataProtectionProvider());
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) CleanupTempDir(_tempContentRoot);
        }
    }

    // Explicit Local mode with empty OIDC settings, no admin.
    private sealed class ExplicitLocalModeFactory : WebApplicationFactory<Program>
    {
        private string? _tempContentRoot;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            _tempContentRoot = CreateTempContentRoot();

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [HostDefaults.ContentRootKey] = _tempContentRoot,
                    ["Authentication:Mode"] = "Local",
                    ["Authentication:Oidc:Authority"] = "",
                    ["Authentication:Oidc:ClientId"] = "",
                    ["Authentication:Oidc:ClientSecret"] = "",
                }));

            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
                services.AddDataProtection().UseEphemeralDataProtectionProvider());
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) CleanupTempDir(_tempContentRoot);
        }
    }

    // Explicit Local mode with empty OIDC settings, admin pre-seeded.
    private sealed class ExplicitLocalModeWithAdminFactory : WebApplicationFactory<Program>
    {
        private string? _tempContentRoot;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            _tempContentRoot = CreateTempContentRoot();
            SeedAdminConfig(_tempContentRoot);

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [HostDefaults.ContentRootKey] = _tempContentRoot,
                    ["Authentication:Mode"] = "Local",
                    ["Authentication:Oidc:Authority"] = "",
                    ["Authentication:Oidc:ClientId"] = "",
                    ["Authentication:Oidc:ClientSecret"] = "",
                }));

            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
                services.AddDataProtection().UseEphemeralDataProtectionProvider());
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) CleanupTempDir(_tempContentRoot);
        }
    }

    // OIDC mode with static metadata configuration.
    private sealed class OidcModeFactory : WebApplicationFactory<Program>
    {
        private string? _tempContentRoot;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            _tempContentRoot = CreateTempContentRoot();

            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [HostDefaults.ContentRootKey] = _tempContentRoot,
                    ["Authentication:Mode"] = "OIDC",
                    ["Authentication:Oidc:Authority"] = "https://idp.example.test/realms/mqttprobe",
                    ["Authentication:Oidc:ClientId"] = "test-client-id",
                    ["Authentication:Oidc:ClientSecret"] = "test-secret",
                    ["Authentication:Oidc:ProviderDisplayName"] = "Test IdP",
                    ["Authentication:Oidc:AdmissionClaim"] = "mqttprobe_access",
                    ["Authentication:Oidc:AcceptedValues:0"] = "admin",
                    ["Authentication:Oidc:PublicBaseUrl"] = "https://mqttprobe.test",
                }));

            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();

                var staticConfig = new OpenIdConnectConfiguration
                {
                    AuthorizationEndpoint =
                        "https://idp.example.test/realms/mqttprobe/protocol/openid-connect/auth",
                    TokenEndpoint =
                        "https://idp.example.test/realms/mqttprobe/protocol/openid-connect/token",
                    Issuer = "https://idp.example.test/realms/mqttprobe",
                };
                staticConfig.SigningKeys.Add(
                    new Microsoft.IdentityModel.Tokens.RsaSecurityKey(RSA.Create(2048)));

                services.Configure<OpenIdConnectOptions>(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.RequireHttpsMetadata = false;
                        options.ConfigurationManager =
                            new Microsoft.IdentityModel.Protocols
                                .StaticConfigurationManager<OpenIdConnectConfiguration>(staticConfig);
                    });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) CleanupTempDir(_tempContentRoot);
        }
    }
}
