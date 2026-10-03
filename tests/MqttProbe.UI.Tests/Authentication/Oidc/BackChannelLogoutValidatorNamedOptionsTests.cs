using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class BackChannelLogoutValidatorNamedOptionsTests
{
    private const string Authority = "https://idp.example.test/realms/mqttprobe";

    private LogoutTokenFactory _tokens = null!;
    private RSA _trustedKey = null!;
    private OpenIdConnectConfiguration _configuration = null!;
    private NamedSchemeAppFactory _factory = null!;
    private readonly List<ServiceProvider> _providers = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _tokens = new LogoutTokenFactory();
        _trustedKey = RSA.Create(2048);
        _configuration = new OpenIdConnectConfiguration { Issuer = LogoutTokenFactory.Issuer };
        _configuration.SigningKeys.Add(new RsaSecurityKey(_trustedKey) { KeyId = LogoutTokenFactory.KeyId });
        _factory = new NamedSchemeAppFactory(_configuration);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory.Dispose();
        _trustedKey.Dispose();
        _tokens.Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _providers.Clear();
    }

    // ── Real production container: options live on the named scheme ─────────

    [Test]
    public void ProductionHost_ConfiguresOpenIdConnectOptionsOnNamedSchemeOnly()
    {
        var monitor = _factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        monitor.Get(OpenIdConnectDefaults.AuthenticationScheme).ClientId
            .Should().Be(LogoutTokenFactory.ClientId);

        // The default-name options carry no configuration at all, which is why
        // IOptions<OpenIdConnectOptions>.Value can never validate a token here.
        _factory.Services.GetRequiredService<IOptions<OpenIdConnectOptions>>().Value.ClientId
            .Should().BeNull();
    }

    [Test]
    public async Task ValidatorResolvedFromProductionHost_AcceptsValidToken()
    {
        var validator = _factory.Services.GetRequiredService<BackChannelLogoutValidator>();
        var token = _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(), _trustedKey);

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.IsValid.Should().BeTrue(result.Reason);
        result.Subject.Should().Be("user-123");
    }

    [Test]
    public async Task ValidatorResolvedFromProductionHost_RejectsUntrustedKey()
    {
        using var rogueKey = RSA.Create(2048);
        var validator = _factory.Services.GetRequiredService<BackChannelLogoutValidator>();
        var token = _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(), rogueKey);

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_signature");
    }

    // ── Key rotation refresh ────────────────────────────────────────────────

    [Test]
    public async Task UnknownSigningKey_RefreshesMetadataAndAcceptsRotatedKey()
    {
        using var oldKey = RSA.Create(2048);
        using var newKey = RSA.Create(2048);
        var manager = new RotatingConfigurationManager(CreateConfiguration(oldKey, "key-1"));
        manager.Publish(CreateConfiguration(newKey, "key-2"));
        var validator = CreateValidator(manager, TimeProvider.System);

        var result = await validator.ValidateAsync(
            _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(), newKey, "key-2"),
            CancellationToken.None);

        result.IsValid.Should().BeTrue(result.Reason);
        manager.RefreshCount.Should().Be(1);
    }

    [Test]
    public async Task MetadataRefresh_IsRateLimitedUntilIntervalElapses()
    {
        using var oldKey = RSA.Create(2048);
        using var secondKey = RSA.Create(2048);
        using var thirdKey = RSA.Create(2048);
        var start = DateTimeOffset.UtcNow;
        var timeProvider = new FakeTimeProvider(start);

        var manager = new RotatingConfigurationManager(CreateConfiguration(oldKey, "key-1"));
        manager.Publish(CreateConfiguration(secondKey, "key-2"));
        var validator = CreateValidator(manager, timeProvider);

        var first = await validator.ValidateAsync(
            _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(), secondKey, "key-2"),
            CancellationToken.None);
        first.IsValid.Should().BeTrue(first.Reason);
        manager.RefreshCount.Should().Be(1);

        // Another unknown key inside the window must not trigger a second fetch.
        manager.Publish(CreateConfiguration(thirdKey, "key-3"));
        var blocked = await validator.ValidateAsync(
            _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(), thirdKey, "key-3"),
            CancellationToken.None);
        blocked.IsValid.Should().BeFalse();
        blocked.Reason.Should().Be("invalid_signature");
        manager.RefreshCount.Should().Be(1);

        timeProvider.Advance(TimeSpan.FromSeconds(61));
        var allowed = await validator.ValidateAsync(
            _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(), thirdKey, "key-3"),
            CancellationToken.None);
        allowed.IsValid.Should().BeTrue(allowed.Reason);
        manager.RefreshCount.Should().Be(2);
    }

    private BackChannelLogoutValidator CreateValidator(
        IConfigurationManager<OpenIdConnectConfiguration> manager,
        TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.ClientId = LogoutTokenFactory.ClientId;
            options.ConfigurationManager = manager;
        });
        services.AddSingleton<BackChannelLogoutValidator>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<BackChannelLogoutValidator>();
    }

    private static OpenIdConnectConfiguration CreateConfiguration(RSA key, string keyId)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = LogoutTokenFactory.Issuer };
        configuration.SigningKeys.Add(new RsaSecurityKey(key) { KeyId = keyId });
        return configuration;
    }

    // Serves cached metadata until RequestRefresh, mirroring how the real
    // ConfigurationManager picks up rotated provider keys.
    private sealed class RotatingConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private OpenIdConnectConfiguration _cached;
        private OpenIdConnectConfiguration _fresh;

        internal RotatingConfigurationManager(OpenIdConnectConfiguration initial)
        {
            _cached = initial;
            _fresh = initial;
        }

        internal int RefreshCount { get; private set; }

        internal void Publish(OpenIdConnectConfiguration configuration) => _fresh = configuration;

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
            => Task.FromResult(_cached);

        public void RequestRefresh()
        {
            RefreshCount++;
            _cached = _fresh;
        }
    }

    // Boots the real MqttProbe.Web composition root in OIDC mode with static
    // metadata, so the validator is resolved exactly as production resolves it.
    private sealed class NamedSchemeAppFactory : WebApplicationFactory<Program>
    {
        private readonly OpenIdConnectConfiguration _configuration;

        internal NamedSchemeAppFactory(OpenIdConnectConfiguration configuration)
            => _configuration = configuration;

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Mode"] = "OIDC",
                    ["Authentication:Oidc:Authority"] = Authority,
                    ["Authentication:Oidc:ClientId"] = LogoutTokenFactory.ClientId,
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
                services.Configure<OpenIdConnectOptions>(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.RequireHttpsMetadata = false;
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(_configuration);
                    });
            });
        }
    }
}
