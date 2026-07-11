using MqttProbe.Models.Chart;
using MqttProbe.Models.Configuration;
using MqttProbe.Models.Mqtt;
using MqttProbe.Models.Sparkplug;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Web.Services;

namespace MqttProbe.Shared.Tests.Services.Authentication;

[TestFixture]
public class SingleAdminUserAuthServiceTests
{
    private ISettingsStore _mockConfig = null!;
    private SingleAdminUserAuthService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _mockConfig = Substitute.For<ISettingsStore>();
        _mockConfig.Config.Returns(new AppConfiguration
        {
            Auth = new Auth { Username = "admin", PasswordHash = PasswordHasher.Hash("correct") }
        });
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
        _mockConfig.SetPasswordAsync("admin", "newpass").Returns(Task.CompletedTask);

        var result = await _service.ChangePasswordAsync("admin", "correct", "newpass");

        result.Succeeded.Should().BeTrue();
        await _mockConfig.Received(1).SetPasswordAsync("admin", "newpass");
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
        _mockConfig.Config.Returns(new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        });

        var users = await _service.GetUsersAsync();

        users.Should().BeEmpty();
    }

    [Test]
    public async Task CreateUserAsync_NoExistingHash_SetsPasswordAndSucceeds()
    {
        _mockConfig.Config.Returns(new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        });
        _mockConfig.SetPasswordAsync("admin", "pass").Returns(Task.CompletedTask);

        var result = await _service.CreateUserAsync("admin", "pass", AppRoles.Admin);

        result.Succeeded.Should().BeTrue();
        await _mockConfig.Received(1).SetPasswordAsync("admin", "pass");
    }

    [Test]
    public async Task CreateUserAsync_WhenPasswordPersistenceFails_DoesNotReturnSuccess()
    {
        _mockConfig.Config.Returns(new AppConfiguration
        {
            Auth = new Auth { Username = "", PasswordHash = "" }
        });
        _mockConfig.SetPasswordAsync("admin", "pass")
            .Returns<Task>(_ => throw new IOException("persistence failed"));

        var act = async () => await _service.CreateUserAsync("admin", "pass", AppRoles.Admin);

        await act.Should().ThrowAsync<IOException>();
    }

    [Test]
    public async Task CreateUserAsync_ExistingHash_ReturnsFailed()
    {
        var result = await _service.CreateUserAsync("second", "pass", AppRoles.Admin);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("one user");
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
}
