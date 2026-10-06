using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Security;
using MqttProbe.Pages;
using MqttProbe.Web.Authentication;
using AuthOptions = MqttProbe.Web.Authentication.AuthenticationOptions;

namespace MqttProbe.UI.Tests.Pages;

[TestFixture]
public class LoginModelTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private IAuthSettings _mockConfig = null!;
    private IUserAuthService _mockAuth = null!;
    private IUiSettings _mockUiSettings = null!;
    private AppSessionCoordinator _coordinator = null!;

    [SetUp]
    public void Setup()
    {
        _mockConfig = Substitute.For<IAuthSettings>();
        var config = new AppConfiguration();
        _mockConfig.Auth.Returns(config.Auth);
        _mockAuth = Substitute.For<IUserAuthService>();
        _mockUiSettings = Substitute.For<IUiSettings>();
        _mockUiSettings.Ui.Returns(new UiPreferences());
        _coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
    }

    [TearDown]
    public void TearDown()
    {
        _coordinator?.Dispose();
    }

    private LoginModel Create(
        AuthOptions? authOptions = null,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        ILogger<LoginModel>? logger = null)
        => new(_mockConfig, _mockAuth, _mockUiSettings, Options.Create(authOptions ?? new AuthOptions()), _coordinator,
            oidcOptionsMonitor, logger);

    private static PageContext PageContextWithAuth(IAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authService);
        return new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } };
    }

    // ── Local mode parity ────────────────────────────────────────────────────

    [Test]
    public void OnGet_LocalMode_NoPasswordHash_RedirectsToSetup()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "" } };
        _mockConfig.Auth.Returns(config.Auth);

        var result = Create().OnGet();

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Setup");
    }

    [Test]
    public void OnGet_LocalMode_PasswordHashSet_ReturnsPage()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);

        var result = Create().OnGet();

        result.Should().BeOfType<PageResult>();
    }

    [Test]
    public void OnGet_LocalMode_PasswordHashSet_SetsReturnUrl()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(returnUrl: "/dashboard");

        model.ReturnUrl.Should().Be("/dashboard");
    }

    [Test]
    public void OnGet_LocalMode_PasswordHashSet_NoReturnUrl_DefaultsToRoot()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet();

        model.ReturnUrl.Should().Be("/");
    }

    [Test]
    public void LoginModel_UsesLoginRateLimitPolicy()
    {
        var attribute = typeof(LoginModel)
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .OfType<EnableRateLimitingAttribute>()
            .SingleOrDefault();

        attribute.Should().NotBeNull();
        attribute!.PolicyName.Should().Be(LoginModel.RateLimitPolicyName);
    }

    [Test]
    public async Task OnPost_LocalMode_InvalidCredentials_SetsErrorMessageAndReturnsPage()
    {
        _mockAuth.ValidateCredentialsAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var model = Create();
        var result = await model.OnPostAsync("user", "wrong", null);

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task OnPost_LocalMode_ValidCredentials_SignsInAndReturnsLocalRedirect()
    {
        _mockAuth.ValidateCredentialsAsync("admin", "correct").Returns(true);

        var authService = Substitute.For<IAuthenticationService>();
        authService.SignInAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = Create();
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync("admin", "correct", "/");

        result.Should().BeOfType<LocalRedirectResult>();
        await authService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Is<ClaimsPrincipal>(principal =>
                principal!.Identity!.Name == "admin" && principal.IsInRole(AppRoles.Admin)),
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_LocalMode_RememberMeFalse_UsesSessionCookie()
    {
        _mockAuth.ValidateCredentialsAsync("admin", "correct").Returns(true);

        var authService = Substitute.For<IAuthenticationService>();
        authService.SignInAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = Create();
        model.RememberMe = false;
        model.PageContext = PageContextWithAuth(authService);

        await model.OnPostAsync("admin", "correct", "/");

        await authService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Is<AuthenticationProperties>(properties =>
                !properties!.IsPersistent && properties.RedirectUri == "/"));
    }

    [Test]
    public async Task OnPost_LocalMode_RememberMeTrue_UsesPersistentCookie()
    {
        _mockAuth.ValidateCredentialsAsync("admin", "correct").Returns(true);

        var authService = Substitute.For<IAuthenticationService>();
        authService.SignInAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = Create();
        model.RememberMe = true;
        model.PageContext = PageContextWithAuth(authService);

        await model.OnPostAsync("admin", "correct", "/");

        await authService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Is<AuthenticationProperties>(properties =>
                properties!.IsPersistent && properties.RedirectUri == "/"));
    }

    [Test]
    public async Task OnPost_LocalMode_NullReturnUrl_DefaultsToRoot()
    {
        _mockAuth.ValidateCredentialsAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var model = Create();

        await model.OnPostAsync("user", "wrong", null);

        model.ReturnUrl.Should().Be("/");
    }

    [Test]
    public void IsAccessibleFontProfile_WhenStandard_ReturnsFalse()
    {
        _mockUiSettings.Ui.Returns(new UiPreferences { FontProfile = FontProfiles.Standard });

        Create().IsAccessibleFontProfile.Should().BeFalse();
    }

    [Test]
    public void IsAccessibleFontProfile_WhenAccessible_ReturnsTrue()
    {
        _mockUiSettings.Ui.Returns(new UiPreferences { FontProfile = FontProfiles.Accessible });

        Create().IsAccessibleFontProfile.Should().BeTrue();
    }

    // ── OIDC mode ────────────────────────────────────────────────────────────

    [Test]
    public void OnGet_OidcMode_NeverRedirectsToSetup()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "" } };
        _mockConfig.Auth.Returns(config.Auth);
        var authOptions = new AuthOptions { Mode = "OIDC" };

        var result = Create(authOptions).OnGet();

        result.Should().BeOfType<PageResult>();
    }

    [Test]
    public void OnGet_OidcMode_ExposesProviderDisplayName()
    {
        var authOptions = new AuthOptions
        {
            Mode = "OIDC",
            Oidc = new OidcOptions { ProviderDisplayName = "Keycloak" }
        };

        var model = Create(authOptions);

        model.ProviderDisplayName.Should().Be("Keycloak");
    }

    [Test]
    public void OnGet_OidcMode_IsOidcModeReturnsTrue()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };

        var model = Create(authOptions);

        model.IsOidcMode.Should().BeTrue();
    }

    [Test]
    public async Task OnPost_OidcMode_NeverValidatesLocalPassword()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var model = Create(authOptions);

        var result = await model.OnPostAsync("admin", "password", "/");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().BeNull();
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_ReturnsChallenge()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var configManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new OpenIdConnectConfiguration());
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = configManager });

        var model = Create(authOptions, oidcMonitor);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostChallengeAsync("/dashboard");

        var challenge = result.Should().BeOfType<ChallengeResult>().Subject;
        challenge.Properties.Should().NotBeNull();
        challenge.Properties!.RedirectUri.Should().Be("/dashboard");
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_ClearsOldCookie()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var configManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new OpenIdConnectConfiguration());
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = configManager });

        var model = Create(authOptions, oidcMonitor);
        model.PageContext = PageContextWithAuth(authService);

        await model.OnPostChallengeAsync("/");

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPostChallenge_LocalMode_ReturnsPage()
    {
        var authOptions = new AuthOptions { Mode = "Local" };
        var model = Create(authOptions);

        var result = await model.OnPostChallengeAsync("/");

        result.Should().BeOfType<PageResult>();
    }

    // ── OIDC metadata preflight ──────────────────────────────────────────────

    [Test]
    public async Task OnPostChallenge_OidcMode_MetadataAvailable_ReturnsChallenge()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var configManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new OpenIdConnectConfiguration());

        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var model = Create(authOptions, oidcMonitor);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostChallengeAsync("/");

        result.Should().BeOfType<ChallengeResult>();
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_MetadataFailure_RedirectsWithSafeError()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };

        var configManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns<Task<OpenIdConnectConfiguration>>(_ => throw new HttpRequestException("Connection refused"));

        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var model = Create(authOptions, oidcMonitor);
        model.PageContext = PageContextWithAuth(Substitute.For<IAuthenticationService>());

        var result = await model.OnPostChallengeAsync("/dashboard");

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
        model.ReturnUrl.Should().Be("/dashboard");
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_MetadataFailure_ResultDoesNotContainExceptionDetails()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };

        var configManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns<Task<OpenIdConnectConfiguration>>(_ => throw new HttpRequestException("SecretAuthority.example.com refused"));

        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var model = Create(authOptions, oidcMonitor);
        model.PageContext = PageContextWithAuth(Substitute.For<IAuthenticationService>());

        var result = await model.OnPostChallengeAsync("/");

        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Login");

        var routeValues = redirect.RouteValues;
        routeValues.Should().ContainKey("error");
        routeValues["error"].Should().Be("provider_unavailable");
        routeValues.Should().NotContainValue("SecretAuthority");
        routeValues.Should().NotContainValue("HttpRequestException");
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_NullMonitor_RedirectsWithSafeError()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var model = Create(authOptions, oidcOptionsMonitor: null);
        model.PageContext = PageContextWithAuth(Substitute.For<IAuthenticationService>());

        var result = await model.OnPostChallengeAsync("/dashboard");

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
        model.ReturnUrl.Should().Be("/dashboard");
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_NullConfigurationManager_RedirectsWithSafeError()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };

        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = null });

        var model = Create(authOptions, oidcMonitor);
        model.PageContext = PageContextWithAuth(Substitute.For<IAuthenticationService>());

        var result = await model.OnPostChallengeAsync("/dashboard");

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
        model.ReturnUrl.Should().Be("/dashboard");
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_MetadataFailure_LogsWarningWithSanitizedException()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var logger = new CapturingLogger();

        var originalException = new HttpRequestException("SecretAuthority.example.com refused");
        var configManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns<Task<OpenIdConnectConfiguration>>(_ => throw originalException);

        var oidcOptions = new OpenIdConnectOptions { ConfigurationManager = configManager };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var model = Create(authOptions, oidcMonitor, logger);
        model.PageContext = PageContextWithAuth(Substitute.For<IAuthenticationService>());

        await model.OnPostChallengeAsync("/");

        logger.Calls.Should().ContainSingle(c => c.Level == LogLevel.Warning);
        var call = logger.Calls.Single(c => c.Level == LogLevel.Warning);
        call.Exception.Should().BeOfType<InvalidOperationException>();
        call.Exception!.Message.Should().Be("OIDC metadata unavailable.");
        call.Exception.InnerException.Should().BeNull();
        call.Exception.Should().NotBeSameAs(originalException);
        call.Message.Should().NotContain("SecretAuthority");
        call.Message.Should().NotContain("HttpRequestException");
        call.Message.Should().NotContain("test-secret");
    }

    [Test]
    public async Task OnPostChallenge_OidcMode_RequestAborted_RethrowsOperationCanceled()
    {
        var authOptions = new AuthOptions { Mode = "OIDC" };

        var configManager = Substitute.For<IConfigurationManager<OpenIdConnectConfiguration>>();
        configManager.GetConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns<Task<OpenIdConnectConfiguration>>(_ => throw new OperationCanceledException());

        var oidcOptions = new OpenIdConnectOptions { ConfigurationManager = configManager };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var model = Create(authOptions, oidcMonitor);
        var httpContext = new DefaultHttpContext();
        httpContext.RequestAborted = new CancellationToken(true);
        model.PageContext = new PageContext { HttpContext = httpContext };

        await model.Invoking(m => m.OnPostChallengeAsync("/"))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ── ReturnUrl validation ─────────────────────────────────────────────────

    [Test]
    public void OnGet_HostReturnUrl_DefaultsToRoot()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(returnUrl: "https://evil.com/steal");

        model.ReturnUrl.Should().Be("/");
    }

    [Test]
    public void OnGet_LocalUrl_PreservesReturnUrl()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(returnUrl: "/dashboard");

        model.ReturnUrl.Should().Be("/dashboard");
    }

    // ── Error categories ─────────────────────────────────────────────────────

    [Test]
    public void OnGet_ProviderUnavailableError_SetsSafeMessage()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(error: "provider_unavailable");

        model.ErrorMessage.Should().NotBeNullOrEmpty();
        model.ErrorMessage.Should().NotContain("exception");
        model.ErrorMessage.Should().NotContain("token");
    }

    [Test]
    public void OnGet_TimeoutError_SetsSafeMessage()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(error: "timeout");

        model.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void OnGet_AuthenticationFailedError_SetsSafeMessage()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(error: "authentication_failed");

        model.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void OnGet_UnknownError_SetsGenericSafeMessage()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(error: "something_unknown");

        model.ErrorMessage.Should().NotBeNullOrEmpty();
        model.ErrorMessage.Should().NotContain("something_unknown");
    }

    [Test]
    public void OnGet_SignOutIncompleteError_WarnsProviderSessionMayRemain()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(error: LogoutModel.SignOutIncompleteError);

        model.ErrorMessage.Should().NotBeNullOrEmpty();
        model.ErrorMessage.Should().Contain("identity provider session could not be ended");
        model.ErrorMessage.Should().NotContain("exception");
    }

    private sealed class CapturingLogger : ILogger<LoginModel>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Calls { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Calls.Add((logLevel, exception, formatter(state, exception)));
        }
    }
}
