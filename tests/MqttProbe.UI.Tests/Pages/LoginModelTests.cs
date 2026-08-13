using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Chart;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Chart;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Metrics;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Security;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.Pages;

namespace MqttProbe.UI.Tests.Pages;

[TestFixture]
public class LoginModelTests
{
    private IAuthSettings _mockConfig = null!;
    private IUserAuthService _mockAuth = null!;
    private IUiSettings _mockUiSettings = null!;

    [SetUp]
    public void Setup()
    {
        _mockConfig = Substitute.For<IAuthSettings>();
        var config = new AppConfiguration();
        _mockConfig.Auth.Returns(config.Auth);
        _mockAuth = Substitute.For<IUserAuthService>();
        _mockUiSettings = Substitute.For<IUiSettings>();
        _mockUiSettings.Ui.Returns(new UiPreferences());
    }

    private LoginModel Create() => new(_mockConfig, _mockAuth, _mockUiSettings);

    private static PageContext PageContextWithAuth(IAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authService);
        return new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } };
    }

    [Test]
    public void OnGet_NoPasswordHash_RedirectsToSetup()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "" } };
        _mockConfig.Auth.Returns(config.Auth);

        var result = Create().OnGet();

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Setup");
    }

    [Test]
    public void OnGet_PasswordHashSet_ReturnsPage()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);

        var result = Create().OnGet();

        result.Should().BeOfType<PageResult>();
    }

    [Test]
    public void OnGet_PasswordHashSet_SetsReturnUrl()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);
        var model = Create();

        model.OnGet(returnUrl: "/dashboard");

        model.ReturnUrl.Should().Be("/dashboard");
    }

    [Test]
    public void OnGet_PasswordHashSet_NoReturnUrl_DefaultsToRoot()
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
                principal!.Identity!.Name == "admin" && principal.IsInRole(AppRoles.Admin)),
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
                !properties!.IsPersistent && properties.RedirectUri == "/"));
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
                properties!.IsPersistent && properties.RedirectUri == "/"));
    }

    [Test]
    public async Task OnPost_NullReturnUrl_DefaultsToRoot()
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
}
