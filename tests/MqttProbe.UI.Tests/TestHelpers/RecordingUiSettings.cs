using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Services.Configuration;

namespace MqttProbe.UI.Tests.TestHelpers;

public sealed class RecordingUiSettings : IUiSettings
{
    public event Action? UiPreferencesChanged;

    public UiPreferences Ui { get; } = new();

    public List<MessageTimePreferences> SavedMessageTimes { get; } = [];

    public Task SetMessageTimeAsync(MessageTimePreferences preferences)
    {
        var next = MessageTimePreferences.Normalize(preferences);
        Ui.MessageTime = next;
        SavedMessageTimes.Add(next);
        UiPreferencesChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetThemeAsync(string theme)
    {
        Ui.Theme = theme;
        UiPreferencesChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetFontProfileAsync(string fontProfile)
    {
        Ui.FontProfile = fontProfile;
        UiPreferencesChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetAutoResubscribeAsync(bool autoResubscribe)
    {
        Ui.AutoResubscribe = autoResubscribe;
        UiPreferencesChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetGroupSparkplugTopicsByNodeAndDeviceAsync(bool value)
    {
        Ui.GroupSparkplugTopicsByNodeAndDevice = value;
        UiPreferencesChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task DismissHintAsync(string hintId)
    {
        if (!Ui.DismissedHints.Contains(hintId))
            Ui.DismissedHints.Add(hintId);
        return Task.CompletedTask;
    }

    public bool IsHintDismissed(string hintId) => Ui.DismissedHints.Contains(hintId);
}
