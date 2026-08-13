namespace MqttProbe.Core.Services.Security;

public static class PasswordPolicy
{
    public const int MinLength = 12;

    public static string MinLengthErrorMessage =>
        $"Password must be at least {MinLength} characters.";

    public static string? ValidateLength(string? password)
    {
        if (password is null || password.Length < MinLength)
            return MinLengthErrorMessage;
        return null;
    }
}
