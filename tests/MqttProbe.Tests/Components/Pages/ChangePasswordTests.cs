using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Components.Pages;
using MqttProbe.Models.Configuration;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.TestHelpers;

namespace MqttProbe.Shared.Tests.Components.Pages;

[TestFixture]
public class ChangePasswordTests : BunitTestContext
{
    private ISettingsStore _mockCfg = null!;
    private IUserAuthService _mockAuth = null!;

    [SetUp]
    public void SetUp()
    {
        _mockCfg = Substitute.For<ISettingsStore>();
        _mockCfg.Config.Returns(new AppConfiguration
        {
            Auth = new Auth { Username = "admin", PasswordHash = "hash" }
        });

        _mockAuth = Substitute.For<IUserAuthService>();

        Services.AddSingleton(_mockCfg);
        Services.AddSingleton(_mockAuth);

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
        cut.Instance._newPassword = "newPassword1!";
        cut.Instance._confirmPassword = "newPassword1!";

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
        cut.Instance._newPassword = "abcdefghijkl1";
        cut.Instance._confirmPassword = "different12345";

        cut.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();

        cut.Markup.Should().Contain("Passwords do not match.");
    }

    [Test]
    public void Submit_WithShortNewPassword_ShowsError_AndDoesNotCallAuth()
    {
        var cut = RenderPage();
        cut.Instance._currentPassword = "current1!";
        cut.Instance._newPassword = "short1!";
        cut.Instance._confirmPassword = "short1!";

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
        cut.Instance._currentPassword = "current1!";
        cut.Instance._newPassword = "abcdefghij12";
        cut.Instance._confirmPassword = "abcdefghij12";

        await cut.InvokeAsync(cut.Instance.Submit);

        await _mockAuth.Received(1).ChangePasswordAsync("admin", "current1!", "abcdefghij12");
    }

    [Test]
    public async Task Submit_WithValidInputs_CallsChangePasswordAndNavigatesToSettings()
    {
        _mockAuth.ChangePasswordAsync("admin", "current1!", "newPassword1!")
            .Returns(Task.FromResult(new AuthServiceResult(true)));

        var cut = RenderPage();
        cut.Instance._currentPassword = "current1!";
        cut.Instance._newPassword = "newPassword1!";
        cut.Instance._confirmPassword = "newPassword1!";

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
}
