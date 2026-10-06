using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Pages;
using MqttProbe.Web.Authentication;
using AuthOptions = MqttProbe.Web.Authentication.AuthenticationOptions;

namespace MqttProbe.UI.Tests.Pages;

[TestFixture]
public class LogoutModelTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static PageContext PageContextWithAuth(
        IAuthenticationService authService,
        string? idToken = null,
        ClaimsPrincipal? user = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authService);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (user is not null)
        {
            httpContext.User = user;
        }

        return new PageContext { HttpContext = httpContext };
    }

    private static ClaimsPrincipal CreateSessionPrincipal(AppSessionRecord record)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.AppSessionId, record.SessionId),
            new Claim(AuthClaimTypes.AppIssuer, record.Issuer),
            new Claim(AuthClaimTypes.AppSubject, record.Subject)
        ], authenticationType: "OIDC");
        return new ClaimsPrincipal(identity);
    }

    private static LogoutModel CreateModel(
        AuthOptions? options = null,
        AppSessionCoordinator? coordinator = null,
        IOptionsMonitor<OpenIdConnectOptions>? oidcOptionsMonitor = null,
        ILogger<LogoutModel>? logger = null)
    {
        return new LogoutModel(
            Options.Create(options ?? new AuthOptions()),
            coordinator ?? new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8)),
            oidcOptionsMonitor,
            logger);
    }

    private static OidcAuthenticationEvents CreateSignOutEvents()
    {
        var timeProvider = new FakeTimeProvider(_epoch);
        return new OidcAuthenticationEvents(
            new AppSessionCoordinator(timeProvider, TimeSpan.FromHours(8)),
            new DenialStateStore(timeProvider),
            Options.Create(new AuthOptions
            {
                Mode = "OIDC",
                Oidc = new OidcOptions
                {
                    ClientId = "mqttprobe",
                    PublicBaseUrl = "https://localhost:5001"
                }
            }),
            Substitute.For<ILogger<OidcAuthenticationEvents>>());
    }

    private static bool WarningContaining(CapturingLogger logger, string fragment)
        => logger.Calls.Any(call =>
            call.Level == LogLevel.Warning &&
            call.Message.Contains(fragment, StringComparison.Ordinal));

    [Test]
    public void OnGetAsync_IsNotAvailableForSignOut()
    {
        typeof(LogoutModel).GetMethod("OnGetAsync", Type.EmptyTypes).Should().BeNull();
    }

    [Test]
    public void LogoutModel_RequiresAntiforgeryToken()
    {
        typeof(LogoutModel).GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
            .Should().NotBeEmpty();
    }

    [Test]
    public void LogoutModel_AllowsAnonymous()
    {
        typeof(LogoutModel).GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
            .Should().NotBeEmpty();
    }

    [Test]
    public async Task OnPost_LocalMode_SignsOutAndRedirectsToLogin()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = CreateModel();
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_OidcMode_ClearsLocalCookieRegardlessOfCleanupErrors()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var model = CreateModel(authOptions, coordinator);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_OidcMode_DiscoveredEndSession_InvokesRemoteOidcSignOut()
    {
        // Set up IAuthenticationService to return a ticket with id_token
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        // Create a ticket with id_token in properties
        var ticketProperties = new AuthenticationProperties();
        ticketProperties.StoreTokens([
            new AuthenticationToken { Name = "id_token", Value = "test-id-token" }
        ]);
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(), ticketProperties, "OIDC");
        authService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        // Set up ConfigurationManager with EndSessionEndpoint
        var config = new OpenIdConnectConfiguration
        {
            EndSessionEndpoint = "https://idp.example.com/logout"
        };
        var configManager = new MockConfigurationManager(config);
        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var model = CreateModel(authOptions, coordinator, oidcMonitor);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        // (a) Discovered end-session config must invoke the OIDC scheme's remote
        // sign-out, not a local-only redirect.
        var signOut = result.Should().BeOfType<SignOutResult>().Subject;
        signOut.AuthenticationSchemes.Should().Contain(OpenIdConnectDefaults.AuthenticationScheme);
        signOut.AuthenticationSchemes.Should().NotContain(CookieAuthenticationDefaults.AuthenticationScheme);
        signOut.Properties.Should().NotBeNull();
        signOut.Properties!.RedirectUri.Should().Be("/Login");
        signOut.Properties.GetTokenValue("id_token").Should().Be("test-id-token");
    }

    [Test]
    public async Task OnPost_OidcMode_RegisteredCircuit_RevokesWithoutForceLoginNavigation()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var ticketProperties = new AuthenticationProperties();
        ticketProperties.StoreTokens([
            new AuthenticationToken { Name = "id_token", Value = "test-id-token" }
        ]);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(), ticketProperties, "OIDC");
        authService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(new OpenIdConnectOptions
        {
            ConfigurationManager = new MockConfigurationManager(new OpenIdConnectConfiguration
            {
                EndSessionEndpoint = "https://idp.example.com/logout"
            })
        });

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var record = coordinator.CreateSession(
            ExternalIdentity.Create("https://idp.example.com", "user-123", "John Doe"));

        // Real teardown handler bound to a registered circuit, not a stub.
        var authInvalidator = Substitute.For<IAuthenticationStateInvalidator>();
        var loginNotifier = Substitute.For<ILoginNavigationNotifier>();
        var teardownHandler = new ScopedCircuitTeardownHandler(
            null, null, authInvalidator, loginNotifier,
            Substitute.For<ILogger<ScopedCircuitTeardownHandler>>());
        var gate = new RevocableSessionActivityGate();
        var lease = new CircuitLease(gate, teardownHandler);
        lease.TryBind("c1", record);
        coordinator.ValidateAndRegister(record.SessionId, "https://idp.example.com", "user-123", lease)
            .Should().NotBeNull();

        var model = CreateModel(new AuthOptions { Mode = "OIDC" }, coordinator, oidcMonitor);
        model.PageContext = PageContextWithAuth(authService, user: CreateSessionPrincipal(record));

        var result = await model.OnPostAsync();

        // The OIDC sign-out handoff is unchanged: the browser follows the
        // identity provider redirect instead of being pushed to /Login first.
        var signOut = result.Should().BeOfType<SignOutResult>().Subject;
        signOut.AuthenticationSchemes.Should().Contain(OpenIdConnectDefaults.AuthenticationScheme);
        signOut.AuthenticationSchemes.Should().NotContain(CookieAuthenticationDefaults.AuthenticationScheme);
        signOut.Properties.Should().NotBeNull();
        signOut.Properties!.RedirectUri.Should().Be("/Login");
        signOut.Properties.GetTokenValue("id_token").Should().Be("test-id-token");

        gate.IsActive.Should().BeFalse();
        coordinator.GetSession(record.SessionId).Should().BeNull();
        authInvalidator.Received(1).SetAnonymous();
        loginNotifier.DidNotReceive().NotifyForceLogin();

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_OidcMode_AuthentikLikeEndSession_CarriesIdTokenHintAndPublicCallback()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var ticketProperties = new AuthenticationProperties();
        ticketProperties.StoreTokens([
            new AuthenticationToken { Name = "id_token", Value = "test-id-token" }
        ]);
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(), ticketProperties, "OIDC");
        authService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        // Authentik-shaped discovery document as served by the real provider.
        var authentikLikeEndpoint = "https://authentik.localhost:9443/application/o/mqttprobe/end-session/";
        var configManager = new MockConfigurationManager(new OpenIdConnectConfiguration
        {
            EndSessionEndpoint = authentikLikeEndpoint
        });
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions { ConfigurationManager = configManager });

        var model = CreateModel(
            new AuthOptions { Mode = "OIDC" },
            new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8)),
            oidcMonitor);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        var signOut = result.Should().BeOfType<SignOutResult>().Subject;
        signOut.AuthenticationSchemes.Should().Contain(OpenIdConnectDefaults.AuthenticationScheme);
        signOut.Properties.Should().NotBeNull();
        signOut.Properties!.GetTokenValue("id_token").Should().Be("test-id-token");

        // (b) The sign-out redirect event must attach id_token_hint and the
        // public post-logout callback while keeping the discovered endpoint.
        var httpContext = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton(new PublicOriginResolver("https://localhost:5001", "*"));
        httpContext.RequestServices = services.BuildServiceProvider();
        var message = new OpenIdConnectMessage { IssuerAddress = authentikLikeEndpoint };
        var redirectContext = new RedirectContext(
            httpContext,
            new AuthenticationScheme("OpenIdConnect", "OpenIdConnect", typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            signOut.Properties)
        {
            ProtocolMessage = message
        };

        await CreateSignOutEvents().OnRedirectToIdentityProviderForSignOut(redirectContext);

        message.IssuerAddress.Should().Be(authentikLikeEndpoint);
        message.IdTokenHint.Should().Be("test-id-token");
        message.PostLogoutRedirectUri.Should().Be("https://localhost:5001/signout-callback-oidc");
    }

    [Test]
    public async Task OnPost_OidcMode_WithoutIdToken_ReturnsSignOutWhenEndSessionEndpointExists()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        // No id_token in ticket
        var ticketProperties = new AuthenticationProperties();
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(), ticketProperties, "OIDC");
        authService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        // ConfigurationManager with EndSessionEndpoint
        var config = new OpenIdConnectConfiguration
        {
            EndSessionEndpoint = "https://idp.example.com/logout"
        };
        var configManager = new MockConfigurationManager(config);
        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var model = CreateModel(authOptions, coordinator, oidcMonitor);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        result.Should().BeOfType<SignOutResult>();
    }

    [Test]
    public async Task OnPost_OidcMode_NoEndSessionEndpoint_LogsAndReturnsObservableError()
    {
        // Set up IAuthenticationService to return a ticket with id_token
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var ticketProperties = new AuthenticationProperties();
        ticketProperties.StoreTokens([
            new AuthenticationToken { Name = "id_token", Value = "test-id-token" }
        ]);
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(), ticketProperties, "OIDC");
        authService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        // ConfigurationManager with no EndSessionEndpoint
        var config = new OpenIdConnectConfiguration();
        var configManager = new MockConfigurationManager(config);
        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var logger = new CapturingLogger();
        var model = CreateModel(authOptions, coordinator, oidcMonitor, logger);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        // (c) Unavailability is deliberate and observable, never a silent /Login bounce.
        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Login");
        redirect.RouteValues.Should().NotBeNull();
        redirect.RouteValues!["error"].Should().Be(LogoutModel.SignOutIncompleteError);
        WarningContaining(logger, "end_session_endpoint_missing").Should().BeTrue();

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
        await authService.DidNotReceive().SignOutAsync(
            Arg.Any<HttpContext>(),
            OpenIdConnectDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_OidcMode_WithoutIdToken_NoEndSessionEndpoint_LogsAndReturnsObservableError()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        // No id_token in ticket
        var ticketProperties = new AuthenticationProperties();
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(), ticketProperties, "OIDC");
        authService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        // ConfigurationManager with no EndSessionEndpoint
        var config = new OpenIdConnectConfiguration();
        var configManager = new MockConfigurationManager(config);
        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var logger = new CapturingLogger();
        var model = CreateModel(authOptions, coordinator, oidcMonitor, logger);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Login");
        redirect.RouteValues.Should().NotBeNull();
        redirect.RouteValues!["error"].Should().Be(LogoutModel.SignOutIncompleteError);
        WarningContaining(logger, "end_session_endpoint_missing").Should().BeTrue();
    }

    [Test]
    public async Task OnPost_OidcMode_ConfigurationManagerMissing_LogsAndReturnsObservableError()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme)
            .Returns(new OpenIdConnectOptions());

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var logger = new CapturingLogger();
        var model = CreateModel(authOptions, coordinator, oidcMonitor, logger);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Login");
        redirect.RouteValues.Should().NotBeNull();
        redirect.RouteValues!["error"].Should().Be(LogoutModel.SignOutIncompleteError);
        WarningContaining(logger, "configuration_manager_unavailable").Should().BeTrue();

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
        await authService.DidNotReceive().SignOutAsync(
            Arg.Any<HttpContext>(),
            OpenIdConnectDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_OidcMode_UnavailableMetadata_LogsAndReturnsObservableError()
    {
        var localSignOutCompleted = false;
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(_ =>
            {
                localSignOutCompleted = true;
                return Task.CompletedTask;
            });

        var ticketProperties = new AuthenticationProperties();
        ticketProperties.StoreTokens([
            new AuthenticationToken { Name = "id_token", Value = "test-id-token" }
        ]);
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(), ticketProperties, "OIDC");
        authService.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        var configManager = new FailingConfigurationManager(() => localSignOutCompleted);
        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = configManager
        };
        var oidcMonitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        oidcMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme).Returns(oidcOptions);

        var coordinator = new AppSessionCoordinator(new FakeTimeProvider(_epoch), TimeSpan.FromHours(8));
        var authOptions = new AuthOptions { Mode = "OIDC" };
        var logger = new CapturingLogger();
        var model = CreateModel(authOptions, coordinator, oidcMonitor, logger);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        localSignOutCompleted.Should().BeTrue();

        // (c) Metadata failure is logged and surfaced, never a silent /Login bounce.
        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Login");
        redirect.RouteValues.Should().NotBeNull();
        redirect.RouteValues!["error"].Should().Be(LogoutModel.SignOutIncompleteError);
        WarningContaining(logger, "metadata_unavailable").Should().BeTrue();
        logger.Calls.Any(call =>
            call.Exception is not null &&
            call.Exception.Message.Contains("Discovery endpoint unavailable", StringComparison.Ordinal))
            .Should().BeTrue("the underlying discovery exception must be logged");

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
        await authService.DidNotReceive().SignOutAsync(
            Arg.Any<HttpContext>(),
            OpenIdConnectDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_OidcMode_OidcOptionsMonitorMissing_LogsAndReturnsObservableError()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var authOptions = new AuthOptions { Mode = "OIDC" };
        var logger = new CapturingLogger();
        var model = CreateModel(authOptions, oidcOptionsMonitor: null, logger: logger);
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
        redirect.PageName.Should().Be("/Login");
        redirect.RouteValues.Should().NotBeNull();
        redirect.RouteValues!["error"].Should().Be(LogoutModel.SignOutIncompleteError);
        WarningContaining(logger, "oidc_options_unavailable").Should().BeTrue();

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
        await authService.DidNotReceive().SignOutAsync(
            Arg.Any<HttpContext>(),
            OpenIdConnectDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }

    private sealed class MockConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _config;

        public MockConfigurationManager(OpenIdConnectConfiguration config)
        {
            _config = config;
        }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
            => Task.FromResult(_config);

        public void RequestRefresh() { }
    }

    private sealed class FailingConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly Func<bool> _signOutCompleted;

        public FailingConfigurationManager(Func<bool> signOutCompleted)
        {
            _signOutCompleted = signOutCompleted;
        }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            _signOutCompleted().Should().BeTrue("cookie signout must complete before configuration retrieval");
            throw new InvalidOperationException("Discovery endpoint unavailable");
        }

        public void RequestRefresh() { }
    }

    private sealed class CapturingLogger : ILogger<LogoutModel>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Calls { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls.Add((logLevel, exception, formatter(state, exception)));
        }
    }
}
