using MQTTnet.Protocol;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Plugins.Protobuf;
using MudBlazor;

namespace MqttProbe.UI.Components.Browser.Connection;

public partial class ConnectionDialog
{
    private const int MaxOnConnectSubscriptions = 500;

    private async Task OnAutoResubscribeChanged(bool value) =>
        await UiSettings.SetAutoResubscribeAsync(value);

    private bool ShouldApplyLiveSubscriptions() =>
        ManagedMqttClient.IsConnected &&
        SessionState.SelectedConnection is { } active &&
        _selectedConnection.Id == active.Id;

    private void SyncSessionStateSelectedTopics()
    {
        var active = SessionState.SelectedConnection;
        if (active is null)
            return;
        if (_selectedConnection.Id == active.Id)
        {
            active.SubscribedTopics = _selectedConnection.SubscribedTopics
                .Select(t => new SubscribedTopic { Topic = t.Topic, QualityOfServiceLevel = t.QualityOfServiceLevel })
                .ToList();
        }
    }

    private void SyncSessionStateTopicExcludes()
    {
        var active = SessionState.SelectedConnection;
        if (active is null)
            return;
        if (_selectedConnection.Id == active.Id)
        {
            active.TopicExcludes = [.. _selectedConnection.TopicExcludes];
        }
    }

    private async Task HandleOnConnectEditorAdd((string Topic, MqttQualityOfServiceLevel Qos) request)
    {
        if (!UiSettings.Ui.AutoResubscribe)
        {
            Snackbar.Add("Enable Auto-resubscribe on connect to manage these topics.", Severity.Info);
            return;
        }
        var topic = request.Topic.Trim();
        if (string.IsNullOrWhiteSpace(topic) || topic.Contains('\0') || topic.Length > 65_535)
        {
            Snackbar.Add("Invalid topic", Severity.Warning);
            return;
        }
        if (_selectedConnection.SubscribedTopics.Any(s => string.Equals(s.Topic, topic, StringComparison.Ordinal)))
        {
            Snackbar.Add($"Already saved: {topic}", Severity.Warning);
            return;
        }
        if (_selectedConnection.SubscribedTopics.Count >= MaxOnConnectSubscriptions)
        {
            Snackbar.Add($"Subscription limit ({MaxOnConnectSubscriptions}) reached", Severity.Warning);
            return;
        }

        var isLive = ShouldApplyLiveSubscriptions();
        var alreadyLive = isLive && SubscriptionManager.Subscriptions.Any(s => string.Equals(s.Topic, topic, StringComparison.Ordinal));

        _selectedConnection.SubscribedTopics.Add(new SubscribedTopic { Topic = topic, QualityOfServiceLevel = request.Qos });
        SyncSessionStateSelectedTopics();
        await ConnectionSettings.AddConnectionAsync(_selectedConnection);

        if (isLive && !alreadyLive)
        {
            try
            {
                await SubscriptionManager.Add(topic, request.Qos);
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to subscribe: {ex.Message}", Severity.Error);
            }
        }
        else
        {
            Snackbar.Add($"Saved subscription {topic}", Severity.Success);
        }
    }

    private async Task HandleOnConnectEditorRemove(IReadOnlyList<string> topics)
    {
        if (!UiSettings.Ui.AutoResubscribe)
        {
            Snackbar.Add("Enable Auto-resubscribe on connect to manage these topics.", Severity.Info);
            return;
        }
        var set = topics.ToHashSet(StringComparer.Ordinal);
        _selectedConnection.SubscribedTopics.RemoveAll(s => set.Contains(s.Topic));
        SyncSessionStateSelectedTopics();
        await ConnectionSettings.AddConnectionAsync(_selectedConnection);

        if (ShouldApplyLiveSubscriptions())
        {
            try
            {
                await SubscriptionManager.Remove(topics);
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to unsubscribe: {ex.Message}", Severity.Error);
            }
        }
    }

#pragma warning disable MA0051 // Method is too long — preserved from original Razor file
    private async Task HandleOnConnectExcludeAdd(string topic)
    {
        var trimmed = topic.Trim();

        if (ShouldApplyLiveSubscriptions())
        {
            // Active connection: validate centrally via the service, then use the
            // service as the sole persistence/live-apply path.
            var validation = TopicExcludeService.ValidateAdd(trimmed);
            if (!validation.IsValid)
            {
                if (validation.Feedback is { } fb)
                    ShowExcludeFeedback(fb);
                return;
            }

            try
            {
                var result = await TopicExcludeService.Add(trimmed);
                if (!result.IsValid)
                {
                    if (result.Feedback is { } fb)
                        ShowExcludeFeedback(fb);
                    return;
                }

                // Service persisted successfully; sync the dialog model so Save stays coherent.
                _selectedConnection.TopicExcludes.Add(trimmed);
                SyncSessionStateTopicExcludes();
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to add exclude: {ex.Message}", Severity.Error);
            }
        }
        else
        {
            // Inactive connection: validate syntax centrally, but duplicate/cap
            // validation is against the profile's own list, not the live service state.
            if (string.IsNullOrWhiteSpace(trimmed) || !MqttTopicMatcher.IsValidFilter(trimmed))
            {
                Snackbar.Add("Invalid topic", Severity.Warning);
                return;
            }
            if (_selectedConnection.TopicExcludes.Any(s => string.Equals(s, trimmed, StringComparison.Ordinal)))
            {
                Snackbar.Add($"Already excluded: {trimmed}", Severity.Warning);
                return;
            }
            const int maxExcludes = 500;
            if (_selectedConnection.TopicExcludes.Count >= maxExcludes)
            {
                Snackbar.Add($"Exclude limit ({maxExcludes}) reached", Severity.Warning);
                return;
            }

            // Transactional: persist a candidate clone first, then mutate only on success.
            var candidate = _selectedConnection.Clone();
            candidate.TopicExcludes.Add(trimmed);
            try
            {
                await ConnectionSettings.AddConnectionAsync(candidate);
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to save exclude: {ex.Message}", Severity.Error);
                return;
            }

            _selectedConnection.TopicExcludes.Add(trimmed);
            SyncSessionStateTopicExcludes();
            Snackbar.Add($"Saved exclude {trimmed}", Severity.Success);
        }
    }
#pragma warning restore MA0051

    private async Task HandleOnConnectExcludeRemove(IReadOnlyList<string> topics)
    {
        if (ShouldApplyLiveSubscriptions())
        {
            // Active connection: service is the sole persistence and live-apply path.
            try
            {
                var result = await TopicExcludeService.Remove(topics);
                if (!result.IsValid)
                {
                    if (result.Feedback is { } fb)
                        ShowExcludeFeedback(fb);
                    return;
                }

                // Service persisted successfully; sync the dialog model.
                var set = topics.ToHashSet(StringComparer.Ordinal);
                _selectedConnection.TopicExcludes.RemoveAll(s => set.Contains(s));
                SyncSessionStateTopicExcludes();
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to remove excludes: {ex.Message}", Severity.Error);
            }
        }
        else
        {
            // Inactive connection: transactional — persist a candidate first.
            var set = topics.ToHashSet(StringComparer.Ordinal);
            var candidate = _selectedConnection.Clone();
            candidate.TopicExcludes.RemoveAll(s => set.Contains(s));
            try
            {
                await ConnectionSettings.AddConnectionAsync(candidate);
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to remove excludes: {ex.Message}", Severity.Error);
                return;
            }

            _selectedConnection.TopicExcludes.RemoveAll(s => set.Contains(s));
            SyncSessionStateTopicExcludes();
        }
    }

    private void ShowExcludeFeedback(Core.UserNotification feedback) =>
        Snackbar.Add(feedback.Message,
            feedback.Severity == Core.UserNotificationSeverity.Warning
                ? Severity.Warning
                : Severity.Error);
}
