using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Authentication;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Tests.Services.Authentication;

[TestFixture]
public class SingleAdminUserAuthServiceTests
{
    private IAuthSettings _mockConfig = null!;
    private SingleAdminUserAuthService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _mockConfig = Substitute.For<IAuthSettings>();
        var config = new AppConfiguration
        {
            Auth = new Auth { Username = "admin", PasswordHash = PasswordHasher.Hash("correct") }
        };
        _mockConfig.Auth.Returns(config.Auth);
        _service = new SingleAdminUserAuthService(_mockConfig);
    }

    [Test]
    public void SupportsMultipleUsers_ReturnsFalse()
    {
        _service.SupportsMultipleUsers.Should().BeFalse();
    }

    [Test]
    public async Task ValidateCredentialsAsync_ValidCredentials_ReturnsTrue()
    {
        _mockConfig.VerifyCredentials("admin", "correct").Returns(true);

        var result = await _service.ValidateCredentialsAsync("admin", "correct");

        result.Should().BeTrue();
    }

    [Test]
    public async Task ValidateCredentialsAsync_InvalidCredentials_ReturnsFalse()
    {
        _mockConfig.VerifyCredentials("admin", "wrong").Returns(false);

        var result = await _service.ValidateCredentialsAsync("admin", "wrong");

        result.Should().BeFalse();
    }

    [Test]
    public async Task ChangePasswordAsync_WrongCurrentPassword_ReturnsFailed()
    {
        _mockConfig.VerifyCredentials("admin", "wrong").Returns(false);

        var result = await _service.ChangePasswordAsync("admin", "wrong", "newpass");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("incorrect");
    }

    [Test]
    public async Task ChangePasswordAsync_CorrectCurrentPassword_SavesAndSucceeds()
    {
        _mockConfig.VerifyCredentials("admin", "correct").Returns(true);
        _mockConfig.SetPasswordAsync("admin", "newpassword1234").Returns(Task.CompletedTask);

        var result = await _service.ChangePasswordAsync("admin", "correct", "newpassword1234");

        result.Succeeded.Should().BeTrue();
        await _mockConfig.Received(1).SetPasswordAsync("admin", "newpassword1234");
    }

    [Test]
    public async Task GetUsersAsync_WithConfiguredUser_ReturnsOneAdminEntry()
    {
        var users = await _service.GetUsersAsync();

        users.Should().HaveCount(1);
        users[0].Username.Should().Be("admin");
        users[0].Role.Should().Be(AppRoles.Admin);
    }

    [Test]
    public async Task GetUsersAsync_NoUsernameConfigured_ReturnsEmptyList()
    {
        var config = new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        };
        _mockConfig.Auth.Returns(config.Auth);

        var users = await _service.GetUsersAsync();

        users.Should().BeEmpty();
    }

    [Test]
    public async Task CreateUserAsync_NoExistingHash_SetsPasswordAndSucceeds()
    {
        var config = new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        };
        _mockConfig.Auth.Returns(config.Auth);
        _mockConfig.SetPasswordAsync("admin", "password1234").Returns(Task.CompletedTask);

        var result = await _service.CreateUserAsync("admin", "password1234", AppRoles.Admin);

        result.Succeeded.Should().BeTrue();
        await _mockConfig.Received(1).SetPasswordAsync("admin", "password1234");
    }

    [Test]
    public async Task CreateUserAsync_WhenPasswordPersistenceFails_DoesNotReturnSuccess()
    {
        var config = new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        };
        _mockConfig.Auth.Returns(config.Auth);
        _mockConfig.SetPasswordAsync("admin", "password1234")
            .Returns<Task>(_ => throw new IOException("persistence failed"));

        var act = async () => await _service.CreateUserAsync("admin", "password1234", AppRoles.Admin);

        await act.Should().ThrowAsync<IOException>();
    }

    [Test]
    public async Task CreateUserAsync_ExistingHash_ReturnsFailed()
    {
        var result = await _service.CreateUserAsync("second", "pass", AppRoles.Admin);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Not supported");
    }

    [Test]
    public async Task DeleteUserAsync_ReturnsNotSupported()
    {
        var result = await _service.DeleteUserAsync("admin");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Not supported");
    }

    [Test]
    public async Task UpdateUserRoleAsync_ReturnsNotSupported()
    {
        var result = await _service.UpdateUserRoleAsync("admin", AppRoles.Operator);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Not supported");
    }

    [Test]
    public async Task CreateUserAsync_PasswordShorterThanMin_ReturnsFailed_DoesNotSetPassword()
    {
        var config = new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        };
        _mockConfig.Auth.Returns(config.Auth);

        var result = await _service.CreateUserAsync("admin", "short", AppRoles.Admin);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("12");
        await _mockConfig.DidNotReceive().SetPasswordAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task CreateUserAsync_PasswordExactlyMinLength_Succeeds()
    {
        var config = new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        };
        _mockConfig.Auth.Returns(config.Auth);
        _mockConfig.SetPasswordAsync("admin", "abcdefghij12").Returns(Task.CompletedTask);

        var result = await _service.CreateUserAsync("admin", "abcdefghij12", AppRoles.Admin);

        result.Succeeded.Should().BeTrue();
        await _mockConfig.Received(1).SetPasswordAsync("admin", "abcdefghij12");
    }

    [Test]
    public async Task ChangePasswordAsync_NewPasswordShorterThanMin_ReturnsFailed_DoesNotSetPassword()
    {
        _mockConfig.VerifyCredentials("admin", "correct").Returns(true);

        var result = await _service.ChangePasswordAsync("admin", "correct", "short");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("12");
        await _mockConfig.DidNotReceive().SetPasswordAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ChangePasswordAsync_NewPasswordExactlyMinLength_Succeeds()
    {
        _mockConfig.VerifyCredentials("admin", "correct").Returns(true);
        _mockConfig.SetPasswordAsync("admin", "abcdefghij12").Returns(Task.CompletedTask);

        var result = await _service.ChangePasswordAsync("admin", "correct", "abcdefghij12");

        result.Succeeded.Should().BeTrue();
        await _mockConfig.Received(1).SetPasswordAsync("admin", "abcdefghij12");
    }
}
