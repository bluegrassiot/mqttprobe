using MqttProbe.Models.Configuration;
using MqttProbe.Services.Security;

namespace MqttProbe.Services.Configuration;

internal sealed class PreferenceSettings(ISettingsDocument document)
    : IUiSettings, IPerformanceSettings, IAuthSettings
{
    public event Action? UiPreferencesChanged;
    public event Action? PerformanceSettingsChanged;

    public UiPreferences Ui => document.Config.Ui;
    public PerformanceSettings Performance => document.Config.Performance;
    public Auth Auth => document.Config.Auth;

    public Task SetThemeAsync(string theme) =>
        SetUiAsync(ui => ui.Theme = theme);

    public Task SetFontFamilyAsync(string fontFamily) =>
        SetUiAsync(ui => ui.FontFamily = fontFamily);

    public Task SetFontAccessibleAsync(bool accessible) =>
        SetUiAsync(ui => ui.FontAccessible = accessible);

    public Task SetAutoResubscribeAsync(bool autoResubscribe) =>
        SetUiAsync(ui => ui.AutoResubscribe = autoResubscribe);

    public Task SetEnrichSparkplugAliasNamesAsync(bool enrich) =>
        SetUiAsync(ui => ui.EnrichSparkplugAliasNames = enrich);

    public Task SetAutoRequestSparkplugRebirthAsync(bool autoRequest) =>
        SetUiAsync(ui => ui.AutoRequestSparkplugRebirth = autoRequest);

    public Task DismissHintAsync(string hintId) =>
        IsHintDismissed(hintId)
            ? Task.CompletedTask
            : SetUiAsync(ui => ui.DismissedHints.Add(hintId));

    public bool IsHintDismissed(string hintId) => Ui.DismissedHints.Contains(hintId);

    public Task SetMaxStoredMessagesAsync(int value) =>
        SetPerformanceAsync(p => p.MaxStoredMessages = value);

    public Task SetMaxMessagesPerSecondAsync(int value) =>
        SetPerformanceAsync(p => p.MaxMessagesPerSecond = value);

    public Task SetMaxDisplayMessagesAsync(int value) =>
        SetPerformanceAsync(p => p.MaxDisplayMessages = value);

    public Task SetMaxTopicNodesAsync(int value) =>
        value < 100
            ? Task.CompletedTask
            : SetPerformanceAsync(p => p.MaxTopicNodes = value);

    public bool VerifyCredentials(string username, string password)
    {
        var auth = Auth;
        if (string.IsNullOrEmpty(auth.PasswordHash)) return false;
        return string.Equals(username, auth.Username, StringComparison.Ordinal)
               && PasswordHasher.Verify(password, auth.PasswordHash);
    }

    public Task SetPasswordAsync(string username, string newPassword)
    {
        // Hashed outside the mutation because PBKDF2 at 600k iterations would otherwise
        // hold the settings lock for the best part of a second.
        var hash = PasswordHasher.Hash(newPassword);
        return document.MutateAndSaveAsync(config =>
        {
            config.Auth.Username = username;
            config.Auth.PasswordHash = hash;
        });
    }

    private async Task SetUiAsync(Action<UiPreferences> mutate)
    {
        await document.MutateAndSaveAsync(config => mutate(config.Ui));
        UiPreferencesChanged?.Invoke();
    }

    private async Task SetPerformanceAsync(Action<PerformanceSettings> mutate)
    {
        await document.MutateAndSaveAsync(config => mutate(config.Performance));
        PerformanceSettingsChanged?.Invoke();
    }
}
