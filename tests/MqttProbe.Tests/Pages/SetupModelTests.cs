using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Pages;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;

namespace MqttProbe.Shared.Tests.Pages;

[TestFixture]
public class SetupModelTests
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

    private SetupModel Create() => new(_mockConfig, _mockAuth);

    private static PageContext PageContextWithAuth(IAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authService);
        return new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } };
    }

    [Test]
    public void OnGet_NoPasswordHash_ReturnsPage()
    {
        _mockConfig.Config.Returns(new AppConfiguration { Auth = new Auth { PasswordHash = "" } });

        var result = Create().OnGet();

        result.Should().BeOfType<PageResult>();
    }

    [Test]
    public void OnGet_PasswordAlreadyConfigured_RedirectsToLogin()
    {
        _mockConfig.Config.Returns(new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } });

        var result = Create().OnGet();

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
    }

    [Test]
    public async Task OnPost_AlreadySetUp_RedirectsToLogin()
    {
        _mockConfig.Config.Returns(new AppConfiguration { Auth = new Auth { PasswordHash = "already-set" } });

        var result = await Create().OnPostAsync("admin", "pass", "pass");

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
    }

    [Test]
    public async Task OnPost_EmptyUsername_SetsErrorAndReturnsPage()
    {
        var model = Create();
        var result = await model.OnPostAsync("", "pass", "pass");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().Contain("Username");
    }

    [Test]
    public async Task OnPost_WhitespaceUsername_SetsErrorAndReturnsPage()
    {
        var model = Create();
        var result = await model.OnPostAsync("   ", "pass", "pass");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task OnPost_EmptyPassword_SetsErrorAndReturnsPage()
    {
        var model = Create();
        var result = await model.OnPostAsync("admin", "", "");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().Contain("Password");
    }

    [Test]
    public async Task OnPost_PasswordMismatch_SetsErrorAndReturnsPage()
    {
        var model = Create();
        var result = await model.OnPostAsync("admin", "password1", "password2");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().Contain("match");
    }

    [Test]
    public async Task OnPost_ValidData_CallsCreateUserAsync()
    {
        _mockAuth.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new AuthServiceResult(true));

        var authService = Substitute.For<IAuthenticationService>();
        authService.SignInAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = Create();
        model.PageContext = PageContextWithAuth(authService);

        await model.OnPostAsync("admin", "secure123", "secure123");

        await _mockAuth.Received(1).CreateUserAsync("admin", "secure123", AppRoles.Admin);
    }

    [Test]
    public async Task OnPost_ValidData_SignsInAndRedirectsToRoot()
    {
        _mockAuth.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new AuthServiceResult(true));

        var authService = Substitute.For<IAuthenticationService>();
        authService.SignInAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = Create();
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync("admin", "secure123", "secure123");

        result.Should().BeOfType<LocalRedirectResult>()
            .Which.Url.Should().Be("/");
        await authService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Is<ClaimsPrincipal>(principal =>
                principal.Identity!.Name == "admin" && principal.IsInRole(AppRoles.Admin)),
            Arg.Any<AuthenticationProperties?>());
    }

    [Test]
    public async Task OnPost_ValidData_UsesSessionCookieForFirstSession()
    {
        _mockAuth.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new AuthServiceResult(true));

        var authService = Substitute.For<IAuthenticationService>();
        authService.SignInAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = Create();
        model.PageContext = PageContextWithAuth(authService);

        await model.OnPostAsync("admin", "secure123", "secure123");

        await authService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Is<AuthenticationProperties>(properties => !properties.IsPersistent));
    }

    [Test]
    public async Task OnPost_CreateUserFails_SetsErrorAndReturnsPage()
    {
        _mockAuth.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new AuthServiceResult(false, "Community edition supports one user."));

        var model = Create();
        var result = await model.OnPostAsync("admin", "secure123", "secure123");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().Contain("Community edition");
    }
}
