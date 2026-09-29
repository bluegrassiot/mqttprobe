using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MqttProbe.Web.Authentication;

namespace MqttProbe.IntegrationTests.Authentication;

internal sealed class OidcWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Uri _appOrigin = new("https://mqttprobe.test");

    private readonly string _authority;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _admissionClaim;
    private readonly string _acceptedValue;
    private readonly string? _publicBaseUrl;
    private readonly string? _allowedHosts;
    private readonly OpenIdConnectConfiguration? _staticConfig;
    private readonly string _tempContentRoot;

    internal OidcWebApplicationFactory(
        string authority,
        string clientId,
        string clientSecret,
        string admissionClaim,
        string acceptedValue,
        string? publicBaseUrl = "https://mqttprobe.test",
        string? allowedHosts = "mqttprobe.test",
        OpenIdConnectConfiguration? staticConfiguration = null)
    {
        _authority = authority;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _admissionClaim = admissionClaim;
        _acceptedValue = acceptedValue;
        _publicBaseUrl = publicBaseUrl;
        _allowedHosts = allowedHosts;
        _staticConfig = staticConfiguration;
        _tempContentRoot = Path.Combine(Path.GetTempPath(), $"mqttprobe-test-{Guid.NewGuid():N}");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Directory.CreateDirectory(Path.Combine(_tempContentRoot, "config"));

        builder.ConfigureHostConfiguration(config =>
        {
            var values = new Dictionary<string, string?>
            {
                [HostDefaults.ContentRootKey] = _tempContentRoot,
                ["Authentication:Mode"] = "OIDC",
                ["Authentication:Oidc:Authority"] = _authority,
                ["Authentication:Oidc:ClientId"] = _clientId,
                ["Authentication:Oidc:ClientSecret"] = _clientSecret,
                ["Authentication:Oidc:ProviderDisplayName"] = "Keycloak",
                ["Authentication:Oidc:AdmissionClaim"] = _admissionClaim,
                ["Authentication:Oidc:AcceptedValues:0"] = _acceptedValue,
            };

            if (_publicBaseUrl is not null)
                values["Authentication:Oidc:PublicBaseUrl"] = _publicBaseUrl;

            if (_allowedHosts is not null)
                values["AllowedHosts"] = _allowedHosts;

            config.AddInMemoryCollection(values);
        });

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IValidateOptions<AuthenticationOptions>>();

            services.AddSingleton<IValidateOptions<AuthenticationOptions>>(sp =>
                new LoopbackOidcOptionsValidator(
                    sp.GetRequiredService<IConfiguration>(), _authority));

            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    options.RequireHttpsMetadata = false;

                    if (_staticConfig is not null)
                    {
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(_staticConfig);
                    }
                });

            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_tempContentRoot))
                    Directory.Delete(_tempContentRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    internal HttpClient CreateOidcClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = _appOrigin,
            AllowAutoRedirect = false,
            HandleCookies = true
        });
}
