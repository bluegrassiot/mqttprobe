namespace MqttProbe.Services.Security;

public interface IUserAuthService
{
    public bool SupportsMultipleUsers { get; }
    public Task<bool> ValidateCredentialsAsync(string username, string password);
    public Task<AuthServiceResult> ChangePasswordAsync(string username, string currentPassword, string newPassword);
    public Task<IReadOnlyList<UserSummary>> GetUsersAsync();
    public Task<AuthServiceResult> CreateUserAsync(string username, string password, string role);
    public Task<AuthServiceResult> DeleteUserAsync(string userId);
    public Task<AuthServiceResult> UpdateUserRoleAsync(string userId, string role);
}

public sealed record UserSummary(string Id, string Username, string Role);

public sealed record AuthServiceResult(bool Succeeded, string? Error = null);
