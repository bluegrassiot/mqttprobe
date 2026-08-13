using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Security;
using MqttProbe.Pages;

namespace MqttProbe.UI.Tests.Pages;

[TestFixture]
public class SetupModelTests
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

    private SetupModel Create() => new(_mockConfig, _mockAuth, _mockUiSettings);

    private static PageContext PageContextWithAuth(IAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(authService);
        return new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } };
    }

    [Test]
    public void OnGet_NoPasswordHash_ReturnsPage()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "" } };
        _mockConfig.Auth.Returns(config.Auth);

        var result = Create().OnGet();

        result.Should().BeOfType<PageResult>();
    }

    [Test]
    public void OnGet_PasswordAlreadyConfigured_RedirectsToLogin()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "hashed" } };
        _mockConfig.Auth.Returns(config.Auth);

        var result = Create().OnGet();

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");
    }

    [Test]
    public async Task OnPost_AlreadySetUp_RedirectsToLogin()
    {
        var config = new AppConfiguration { Auth = new Auth { PasswordHash = "already-set" } };
        _mockConfig.Auth.Returns(config.Auth);

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

        await model.OnPostAsync("admin", "secure123456", "secure123456");

        await _mockAuth.Received(1).CreateUserAsync("admin", "secure123456", AppRoles.Admin);
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

        var result = await model.OnPostAsync("admin", "secure123456", "secure123456");

        result.Should().BeOfType<LocalRedirectResult>()
            .Which.Url.Should().Be("/");
        await authService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Is<ClaimsPrincipal>(principal =>
                principal!.Identity!.Name == "admin" && principal.IsInRole(AppRoles.Admin)),
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

        await model.OnPostAsync("admin", "secure123456", "secure123456");

        await authService.Received(1).SignInAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<ClaimsPrincipal>(),
            Arg.Is<AuthenticationProperties>(properties => !properties!.IsPersistent));
    }

    [Test]
    public async Task OnPost_CreateUserFails_SetsErrorAndReturnsPage()
    {
        _mockAuth.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new AuthServiceResult(false, "Not supported."));

        var model = Create();
        var result = await model.OnPostAsync("admin", "secure123456", "secure123456");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().Contain("Not supported.");
    }

    [Test]
    public async Task OnPost_CreateUserFailsWithMinLengthError_SetsErrorAndReturnsPage()
    {
        _mockAuth.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new AuthServiceResult(false, "Password must be at least 12 characters."));

        var model = Create();
        var result = await model.OnPostAsync("admin", "short", "short");

        result.Should().BeOfType<PageResult>();
        model.ErrorMessage.Should().Contain("12");
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
