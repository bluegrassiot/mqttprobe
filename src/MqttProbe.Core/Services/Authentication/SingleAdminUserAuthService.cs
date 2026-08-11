using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Services.Authentication;

public class SingleAdminUserAuthService(IAuthSettings authSettings) : IUserAuthService
{
    public bool SupportsMultipleUsers => false;

    public Task<bool> ValidateCredentialsAsync(string username, string password)
        => Task.FromResult(authSettings.VerifyCredentials(username, password));

    public async Task<AuthServiceResult> ChangePasswordAsync(
        string username, string currentPassword, string newPassword)
    {
        if (!authSettings.VerifyCredentials(username, currentPassword))
            return new AuthServiceResult(false, "Current password is incorrect.");

        var lengthError = PasswordPolicy.ValidateLength(newPassword);
        if (lengthError is not null)
            return new AuthServiceResult(false, lengthError);

        await authSettings.SetPasswordAsync(username, newPassword);
        return new AuthServiceResult(true);
    }

    public Task<IReadOnlyList<UserSummary>> GetUsersAsync()
    {
        var auth = authSettings.Auth;
        IReadOnlyList<UserSummary> users = string.IsNullOrEmpty(auth.Username)
            ? []
            : [new UserSummary(auth.Username, auth.Username, AppRoles.Admin)];
        return Task.FromResult(users);
    }

    public async Task<AuthServiceResult> CreateUserAsync(string username, string password, string role)
    {
        if (!string.IsNullOrEmpty(authSettings.Auth.PasswordHash))
            return new AuthServiceResult(false, "Not supported.");

        var lengthError = PasswordPolicy.ValidateLength(password);
        if (lengthError is not null)
            return new AuthServiceResult(false, lengthError);

        await authSettings.SetPasswordAsync(username, password);
        return new AuthServiceResult(true);
    }

    public Task<AuthServiceResult> DeleteUserAsync(string userId)
        => Task.FromResult(new AuthServiceResult(false, "Not supported."));

    public Task<AuthServiceResult> UpdateUserRoleAsync(string userId, string role)
        => Task.FromResult(new AuthServiceResult(false, "Not supported."));
}
