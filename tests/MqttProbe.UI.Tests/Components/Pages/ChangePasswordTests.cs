using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Platform;
using MqttProbe.Core.Services.Security;
using MqttProbe.UI.Components.Pages;
using MqttProbe.UI.Tests.TestHelpers;

namespace MqttProbe.UI.Tests.Components.Pages;

[TestFixture]
public class ChangePasswordTests : BunitTestContext
{
    private IAuthSettings _mockCfg = null!;
    private IUserAuthService _mockAuth = null!;
    private IAppInfoService _mockAppInfo = null!;

    [SetUp]
    public void SetUp()
    {
        _mockCfg = Substitute.For<IAuthSettings>();
        var cfg = new AppConfiguration
        {
            Auth = new Auth { Username = "admin", PasswordHash = "hash" }
        };
        _mockCfg.Auth.Returns(cfg.Auth);

        _mockAuth = Substitute.For<IUserAuthService>();

        _mockAppInfo = Substitute.For<IAppInfoService>();
        _mockAppInfo.IsOidcMode.Returns(false);

        Services.AddAuthSettings(_mockCfg);
        Services.AddSingleton(_mockAuth);
        Services.AddSingleton(_mockAppInfo);

        AuthorizationContext.SetAuthorized("admin").SetRoles(AppRoles.Admin);

        EnsureMudProviders();
    }

    private IRenderedComponent<ChangePassword> RenderPage() => Render<ChangePassword>();

    [Test]
    public void Renders_PasswordFields_And_SaveButton()
    {
        var cut = RenderPage();

        cut.Markup.Should().Contain("Current Password");
        cut.Markup.Should().Contain("New Password");
        cut.Markup.Should().Contain("Confirm New Password");
        cut.Markup.Should().Contain("Save Password");
    }

    [Test]
    public async Task Submit_WithWrongCurrentPassword_ShowsError()
    {
        _mockAuth.ChangePasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(new AuthServiceResult(false, "Current password is incorrect.")));

        var cut = RenderPage();
        cut.Instance.NewPassword = "newPassword1!";
        cut.Instance.ConfirmPassword = "newPassword1!";

        await cut.InvokeAsync(cut.Instance.Submit);
        cut.Render();

        cut.Markup.Should().Contain("Current password is incorrect.");
    }

    [Test]
    public void Submit_WithEmptyNewPassword_ShowsError()
    {
        var cut = RenderPage();

        cut.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        cut.Markup.Should().Contain("New password cannot be empty.");
    }

    [Test]
    public void Submit_WithMismatchedPasswords_ShowsError()
    {
        var cut = RenderPage();
        cut.Instance.NewPassword = "abcdefghijkl1";
        cut.Instance.ConfirmPassword = "different12345";

        cut.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        cut.Markup.Should().Contain("Passwords do not match.");
    }

    [Test]
    public void Submit_WithShortNewPassword_ShowsError_AndDoesNotCallAuth()
    {
        var cut = RenderPage();
        cut.Instance.CurrentPassword = "current1!";
        cut.Instance.NewPassword = "short1!";
        cut.Instance.ConfirmPassword = "short1!";

        cut.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        cut.Markup.Should().Contain("at least 12 characters");
        _mockAuth.DidNotReceive().ChangePasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Submit_WithTwelveCharacterPassword_ReachesAuthService()
    {
        _mockAuth.ChangePasswordAsync("admin", "current1!", "abcdefghij12")
            .Returns(Task.FromResult(new AuthServiceResult(true)));

        var cut = RenderPage();
        cut.Instance.CurrentPassword = "current1!";
        cut.Instance.NewPassword = "abcdefghij12";
        cut.Instance.ConfirmPassword = "abcdefghij12";

        await cut.InvokeAsync(cut.Instance.Submit);

        await _mockAuth.Received(1).ChangePasswordAsync("admin", "current1!", "abcdefghij12");
    }

    [Test]
    public async Task Submit_WithValidInputs_CallsChangePasswordAndNavigatesToSettings()
    {
        _mockAuth.ChangePasswordAsync("admin", "current1!", "newPassword1!")
            .Returns(Task.FromResult(new AuthServiceResult(true)));

        var cut = RenderPage();
        cut.Instance.CurrentPassword = "current1!";
        cut.Instance.NewPassword = "newPassword1!";
        cut.Instance.ConfirmPassword = "newPassword1!";

        await cut.InvokeAsync(cut.Instance.Submit);
        cut.Render();

        await _mockAuth.Received(1).ChangePasswordAsync("admin", "current1!", "newPassword1!");
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.Uri.Should().Contain("/?tab=settings");
    }

    [Test]
    public void CancelButton_NavigatesToSettingsTab()
    {
        var cut = RenderPage();

        cut.FindAll("button").First(b => b.TextContent.Contains("Cancel")).Click();

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.Uri.Should().Contain("/?tab=settings");
    }

    [Test]
    public void OidcMode_ShowsPasswordManagementMessage()
    {
        _mockAppInfo.IsOidcMode.Returns(true);

        var cut = RenderPage();

        cut.Markup.Should().Contain("Password management");
        cut.Markup.Should().Contain("identity provider");
    }

    [Test]
    public void OidcMode_HidesPasswordFields()
    {
        _mockAppInfo.IsOidcMode.Returns(true);

        var cut = RenderPage();

        cut.Markup.Should().NotContain("Current Password");
        cut.Markup.Should().NotContain("New Password");
        cut.Markup.Should().NotContain("Save Password");
    }

    [Test]
    public void OidcMode_ShowsBackToSettingsButton()
    {
        _mockAppInfo.IsOidcMode.Returns(true);

        var cut = RenderPage();

        cut.FindAll("button").First(b => b.TextContent.Contains("Back to settings")).Click();

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.Uri.Should().Contain("/?tab=settings");
    }

    [Test]
    public void OidcMode_DoesNotCallChangePasswordService()
    {
        _mockAppInfo.IsOidcMode.Returns(true);

        RenderPage();

        _mockAuth.DidNotReceive().ChangePasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Submit_InOidcMode_DoesNotCallChangePasswordService()
    {
        _mockAppInfo.IsOidcMode.Returns(true);

        var cut = RenderPage();
        cut.Instance.CurrentPassword = "current1!";
        cut.Instance.NewPassword = "newPassword1!";
        cut.Instance.ConfirmPassword = "newPassword1!";

        await cut.InvokeAsync(async () => await cut.Instance.Submit());

        _ = _mockAuth.DidNotReceive().ChangePasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
