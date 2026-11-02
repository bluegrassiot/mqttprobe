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
            active.SparkplugTopicExcludes = [.. _selectedConnection.SparkplugTopicExcludes];
        }
    }

    private bool HasNameCollisionForPersistence()
    {
        var trimmed = _selectedConnection.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        var selfId = _selectedConnection.Id;
        return ConnectionSettings.Connections.Any(c =>
            c.Id != selfId &&
            string.Equals(c.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshSubscriptionBaseline()
    {
        _baselineConnection.SubscribedTopics = _selectedConnection.SubscribedTopics
            .Select(t => new SubscribedTopic { Topic = t.Topic, QualityOfServiceLevel = t.QualityOfServiceLevel })
            .ToList();
    }

    private void RefreshExcludeBaseline()
    {
        _baselineConnection.TopicExcludes = [.. _selectedConnection.TopicExcludes];
        _baselineConnection.SparkplugTopicExcludes = [.. _selectedConnection.SparkplugTopicExcludes];
    }

    private bool IsConnectionSaved() =>
        ConnectionSettings.Connections.Any(c => c.Id == _selectedConnection.Id);

    private MqttProbe.Core.Models.Mqtt.Connection BuildCollectionOnlyCandidate()
    {
        // Build persistence candidate from baseline, patching only collections.
        // This prevents unrelated dirty edits (Host, credentials, TLS, timing, cert)
        // from being silently persisted through collection operations.
        var candidate = _baselineConnection.Clone();
        candidate.SubscribedTopics = _selectedConnection.SubscribedTopics
            .Select(t => new SubscribedTopic { Topic = t.Topic, QualityOfServiceLevel = t.QualityOfServiceLevel })
            .ToList();
        candidate.TopicExcludes = [.. _selectedConnection.TopicExcludes];
        candidate.SparkplugTopicExcludes = [.. _selectedConnection.SparkplugTopicExcludes];
        return candidate;
    }

    private bool ValidateNewSubscriptionTopic(string rawTopic)
    {
        var topic = rawTopic.Trim();
        if (!MqttTopicMatcher.IsValidFilter(topic))
        {
            Snackbar.Add("Invalid topic", Severity.Warning);
            return false;
        }
        if (_selectedConnection.SubscribedTopics.Any(s => string.Equals(s.Topic, topic, StringComparison.Ordinal)))
        {
            Snackbar.Add($"Already saved: {topic}", Severity.Warning);
            return false;
        }
        if (_selectedConnection.SubscribedTopics.Count >= MaxOnConnectSubscriptions)
        {
            Snackbar.Add($"Subscription limit ({MaxOnConnectSubscriptions}) reached", Severity.Warning);
            return false;
        }
        return true;
    }

    private bool ValidateNewExcludeTopic(string trimmed)
    {
        if (string.IsNullOrWhiteSpace(trimmed) || !MqttTopicMatcher.IsValidFilter(trimmed))
        {
            Snackbar.Add("Invalid topic", Severity.Warning);
            return false;
        }
        if (_selectedConnection.TopicExcludes.Any(s => string.Equals(s, trimmed, StringComparison.Ordinal)))
        {
            Snackbar.Add($"Already excluded: {trimmed}", Severity.Warning);
            return false;
        }
        const int maxExcludes = 500;
        var excludeCount = _selectedConnection.TopicExcludes.Count + _selectedConnection.SparkplugTopicExcludes.Count;
        if (excludeCount >= maxExcludes)
        {
            Snackbar.Add($"Exclude limit ({maxExcludes}) reached", Severity.Warning);
            return false;
        }
        return true;
    }

    private async Task<bool> EnsureReadyForCollectionEdit(string context, bool requireSaved)
    {
        if (!await ValidateForm())
        {
            Snackbar.Add($"Fix connection validation errors before {context}.", Severity.Warning);
            return false;
        }
        if (HasNameCollisionForPersistence())
        {
            Snackbar.Add($"Resolve the name collision before {context}.", Severity.Warning);
            return false;
        }
        if (requireSaved && !IsConnectionSaved())
        {
            Snackbar.Add($"Save the connection first before {context}.", Severity.Warning);
            return false;
        }
        return true;
    }

    private async Task ApplyLiveSubscriptionAsync(string topic, MqttQualityOfServiceLevel qos)
    {
        try
        {
            await SubscriptionManager.Add(topic, qos);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to subscribe: {ex.Message}", Severity.Error);
        }
    }

    private async Task HandleOnConnectEditorAdd((string Topic, MqttQualityOfServiceLevel Qos) request)
    {
        if (!UiSettings.Ui.AutoResubscribe)
        {
            Snackbar.Add("Enable Auto-resubscribe on connect to manage these topics.", Severity.Info);
            return;
        }
        if (!ValidateNewSubscriptionTopic(request.Topic)) return;
        if (!await EnsureReadyForCollectionEdit("adding subscriptions", requireSaved: true)) return;

        var topic = request.Topic.Trim();
        var isLive = ShouldApplyLiveSubscriptions();
        var alreadyLive = isLive && SubscriptionManager.Subscriptions.Any(s => string.Equals(s.Topic, topic, StringComparison.Ordinal));

        var candidate = BuildCollectionOnlyCandidate();
        candidate.SubscribedTopics.Add(new SubscribedTopic { Topic = topic, QualityOfServiceLevel = request.Qos });
        try
        {
            await ConnectionSettings.AddConnectionAsync(candidate);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save subscription: {ex.Message}", Severity.Error);
            return;
        }

        _selectedConnection.SubscribedTopics.Add(new SubscribedTopic { Topic = topic, QualityOfServiceLevel = request.Qos });
        SyncSessionStateSelectedTopics();
        RefreshSubscriptionBaseline();

        if (isLive && !alreadyLive)
            await ApplyLiveSubscriptionAsync(topic, request.Qos);
        else
            Snackbar.Add($"Saved subscription {topic}", Severity.Success);
    }

    private async Task HandleOnConnectEditorRemove(IReadOnlyList<string> topics)
    {
        if (!UiSettings.Ui.AutoResubscribe)
        {
            Snackbar.Add("Enable Auto-resubscribe on connect to manage these topics.", Severity.Info);
            return;
        }

        if (!await ValidateForm())
        {
            Snackbar.Add("Fix connection validation errors before removing subscriptions.", Severity.Warning);
            return;
        }
        if (HasNameCollisionForPersistence())
        {
            Snackbar.Add("Resolve the name collision before removing subscriptions.", Severity.Warning);
            return;
        }
        if (!IsConnectionSaved())
        {
            Snackbar.Add("Save the connection first before removing subscriptions.", Severity.Warning);
            return;
        }

        var set = topics.ToHashSet(StringComparer.Ordinal);
        var candidate = BuildCollectionOnlyCandidate();
        candidate.SubscribedTopics.RemoveAll(s => set.Contains(s.Topic));
        try
        {
            await ConnectionSettings.AddConnectionAsync(candidate);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save subscription removal: {ex.Message}", Severity.Error);
            return;
        }

        _selectedConnection.SubscribedTopics.RemoveAll(s => set.Contains(s.Topic));
        SyncSessionStateSelectedTopics();
        RefreshSubscriptionBaseline();

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

    private async Task<bool> AddExcludeLiveAsync(string trimmed)
    {
        var validation = TopicExcludeService.ValidateAdd(trimmed);
        if (!validation.IsValid)
        {
            if (validation.Feedback is { } fb)
                ShowExcludeFeedback(fb);
            return false;
        }

        if (!await EnsureReadyForCollectionEdit("managing excludes", requireSaved: false)) return false;

        try
        {
            var result = await TopicExcludeService.Add(trimmed);
            if (!result.IsValid)
            {
                if (result.Feedback is { } fb)
                    ShowExcludeFeedback(fb);
                return false;
            }

            _selectedConnection.TopicExcludes.Add(trimmed);
            SyncSessionStateTopicExcludes();
            RefreshExcludeBaseline();
            return true;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to add exclude: {ex.Message}", Severity.Error);
            return false;
        }
    }

    private async Task<bool> AddExcludeOfflineAsync(string trimmed)
    {
        if (!ValidateNewExcludeTopic(trimmed)) return false;
        if (!await EnsureReadyForCollectionEdit("managing excludes", requireSaved: true)) return false;

        var candidate = BuildCollectionOnlyCandidate();
        candidate.TopicExcludes.Add(trimmed);
        try
        {
            await ConnectionSettings.AddConnectionAsync(candidate);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save exclude: {ex.Message}", Severity.Error);
            return false;
        }

        _selectedConnection.TopicExcludes.Add(trimmed);
        SyncSessionStateTopicExcludes();
        RefreshExcludeBaseline();
        Snackbar.Add($"Saved exclude {trimmed}", Severity.Success);
        return true;
    }

    private async Task<bool> HandleOnConnectExcludeAdd(string topic)
    {
        var trimmed = topic.Trim();
        var accepted = ShouldApplyLiveSubscriptions()
            ? await AddExcludeLiveAsync(trimmed)
            : await AddExcludeOfflineAsync(trimmed);

        // Plain Func, so Blazor does not re-render us and the editor would stay stale.
        await InvokeAsync(StateHasChanged);
        return accepted;
    }

    private async Task RemoveExcludeLiveAsync(IReadOnlyList<string> topics)
    {
        if (!await EnsureReadyForCollectionEdit("managing excludes", requireSaved: false)) return;

        try
        {
            var result = await TopicExcludeService.Remove(topics);
            if (!result.IsValid)
            {
                if (result.Feedback is { } fb)
                    ShowExcludeFeedback(fb);
                return;
            }

            var set = topics.ToHashSet(StringComparer.Ordinal);
            _selectedConnection.TopicExcludes.RemoveAll(s => set.Contains(s));
            SyncSessionStateTopicExcludes();
            RefreshExcludeBaseline();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to remove excludes: {ex.Message}", Severity.Error);
        }
    }

    private async Task RemoveExcludeOfflineAsync(IReadOnlyList<string> topics)
    {
        if (!await EnsureReadyForCollectionEdit("managing excludes", requireSaved: true)) return;

        var set = topics.ToHashSet(StringComparer.Ordinal);
        var candidate = BuildCollectionOnlyCandidate();
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
        RefreshExcludeBaseline();
    }

    private async Task HandleOnConnectExcludeRemove(IReadOnlyList<string> topics)
    {
        if (ShouldApplyLiveSubscriptions())
            await RemoveExcludeLiveAsync(topics);
        else
            await RemoveExcludeOfflineAsync(topics);
    }

    private async Task HandleOnConnectSparkplugExcludeRemove(SparkplugTopicExclude exclude)
    {
        if (ShouldApplyLiveSubscriptions())
            await RemoveSparkplugExcludeLiveAsync(exclude);
        else
            await RemoveSparkplugExcludeOfflineAsync(exclude);
    }

    private async Task RemoveSparkplugExcludeLiveAsync(SparkplugTopicExclude exclude)
    {
        if (!await EnsureReadyForCollectionEdit("managing excludes", requireSaved: false)) return;

        try
        {
            var target = new TopicBranchTarget(
                TopicBranchKind.Sparkplug, Scope: exclude.Scope, MessageType: exclude.MessageType);
            var result = await TopicExcludeService.RemoveBranches([target]);
            if (!result.IsValid)
            {
                if (result.Feedback is { } feedback)
                    ShowExcludeFeedback(feedback);
                return;
            }

            _selectedConnection.SparkplugTopicExcludes.RemoveAll(item => item == exclude);
            SyncSessionStateTopicExcludes();
            RefreshExcludeBaseline();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to remove Sparkplug exclusion: {ex.Message}", Severity.Error);
        }
    }

    private async Task RemoveSparkplugExcludeOfflineAsync(SparkplugTopicExclude exclude)
    {
        if (!await EnsureReadyForCollectionEdit("managing excludes", requireSaved: true)) return;

        var candidate = BuildCollectionOnlyCandidate();
        candidate.SparkplugTopicExcludes.RemoveAll(item => item == exclude);
        try
        {
            await ConnectionSettings.AddConnectionAsync(candidate);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to remove Sparkplug exclusion: {ex.Message}", Severity.Error);
            return;
        }

        _selectedConnection.SparkplugTopicExcludes.RemoveAll(item => item == exclude);
        SyncSessionStateTopicExcludes();
        RefreshExcludeBaseline();
    }

    private void ShowExcludeFeedback(Core.UserNotification feedback) =>
        Snackbar.Add(feedback.Message,
            feedback.Severity == Core.UserNotificationSeverity.Warning
                ? Severity.Warning
                : Severity.Error);
}
