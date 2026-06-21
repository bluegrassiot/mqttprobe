using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Pages;

namespace MqttProbe.Shared.Tests.Pages;

[TestFixture]
public class LogoutModelTests
{
    private static PageContext PageContextWithAuth(IAuthenticationService authService)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authService);
        return new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() } };
    }

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
    public async Task OnPost_SignsOutAndRedirectsToLogin()
    {
        var authService = Substitute.For<IAuthenticationService>();
        authService.SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(Task.CompletedTask);

        var model = new LogoutModel();
        model.PageContext = PageContextWithAuth(authService);

        var result = await model.OnPostAsync();

        result.Should().BeOfType<RedirectToPageResult>()
            .Which.PageName.Should().Be("/Login");

        await authService.Received(1).SignOutAsync(
            Arg.Any<HttpContext>(),
            CookieAuthenticationDefaults.AuthenticationScheme,
            Arg.Any<AuthenticationProperties?>());
    }
}
