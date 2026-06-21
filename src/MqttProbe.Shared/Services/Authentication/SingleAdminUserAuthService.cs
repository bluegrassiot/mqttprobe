using MqttProbe.Services.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Authentication;

public class SingleAdminUserAuthService(ISettingsStore settingsStore) : IUserAuthService
{
    public bool SupportsMultipleUsers => false;

    public Task<bool> ValidateCredentialsAsync(string username, string password)
        => Task.FromResult(settingsStore.VerifyCredentials(username, password));

    public async Task<AuthServiceResult> ChangePasswordAsync(
        string username, string currentPassword, string newPassword)
    {
        if (!settingsStore.VerifyCredentials(username, currentPassword))
            return new AuthServiceResult(false, "Current password is incorrect.");

        await settingsStore.SetPasswordAsync(username, newPassword);
        return new AuthServiceResult(true);
    }

    public Task<IReadOnlyList<UserSummary>> GetUsersAsync()
    {
        var auth = settingsStore.Config.Auth;
        IReadOnlyList<UserSummary> users = string.IsNullOrEmpty(auth.Username)
            ? []
            : [new UserSummary(auth.Username, auth.Username, AppRoles.Admin)];
        return Task.FromResult(users);
    }

    public async Task<AuthServiceResult> CreateUserAsync(string username, string password, string role)
    {
        if (!string.IsNullOrEmpty(settingsStore.Config.Auth.PasswordHash))
            return new AuthServiceResult(false, "Community edition supports one user.");

        await settingsStore.SetPasswordAsync(username, password);
        return new AuthServiceResult(true);
    }

    public Task<AuthServiceResult> DeleteUserAsync(string userId)
        => Task.FromResult(new AuthServiceResult(false, "Not supported in community edition."));

    public Task<AuthServiceResult> UpdateUserRoleAsync(string userId, string role)
        => Task.FromResult(new AuthServiceResult(false, "Not supported in community edition."));
}
