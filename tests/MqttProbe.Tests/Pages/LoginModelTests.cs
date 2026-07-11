using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Pages;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Pages;

[TestFixture]
public class LoginModelTests
{
    private ISettingsStore _mockConfig = null!;
    private IUserAuthService _mockAuth = null!;

    [SetUp]
    public void Setup()
    {
        _mockConfig = Substitute.For<ISettingsStore>();
        _mockConfig.Config.Returns(new AppConfiguration());
        _mockAuth = Substitute.For<IUserAuthService>();
    }

    private LoginModel Create() => new(_mockConfig, _mockAuth);

    private static PageContext PageContextWithAuth(IAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authService);
        return new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } };
    }

    [Test]
    public void OnGet_NoPasswordHash_RedirectsToSetup()
    {
        _mockConfig.Config.Returns(new AppConfiguration { Auth = new Auth { PasswordHash = "" } });

        var result = Create().OnGet();

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Setup");
    }

    [Test]
    public void OnGet_PasswordHashSet_ReturnsPage()
    {
        _mockConfig.Config.Returns(new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } });

        var result = Create().OnGet();

        result.Should().BeOfType<PageResult>();
    }

    [Test]
    public void OnGet_PasswordHashSet_SetsReturnUrl()
    {
        _mockConfig.Config.Returns(new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } });
        var model = Create();

        model.OnGet(returnUrl: "/dashboard");

        model.ReturnUrl.Should().Be("/dashboard");
    }

    [Test]
    public void OnGet_PasswordHashSet_NoReturnUrl_DefaultsToRoot()
    {
        _mockConfig.Config.Returns(new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } });
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
    public async Task OnPost_InvalidCredentials_SetsErrorMessageAndReturnsPage()
    {
        _mockAuth.ValidateCredentialsAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        var model = Create();
        var result = await model.OnPostAsync("user", "wrong", null);

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task OnPost_ValidCredentials_SignsInAndReturnsLocalRedirect()
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
                principal.Identity!.Name == "admin" && principal.IsInRole(AppRoles.Admin)),
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_ValidCredentials_RememberMeFalse_UsesSessionCookie()
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
                !properties.IsPersistent && properties.RedirectUri == "/"));
    }

    [Test]
    public async Task OnPost_ValidCredentials_RememberMeTrue_UsesPersistentCookie()
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
                properties.IsPersistent && properties.RedirectUri == "/"));
    }

    [Test]
    public async Task OnPost_NullReturnUrl_DefaultsToRoot()
    {
        _mockAuth.ValidateCredentialsAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var model = Create();

        await model.OnPostAsync("user", "wrong", null);

        model.ReturnUrl.Should().Be("/");
    }
}
