using Microsoft.Extensions.Logging;

namespace MqttProbe.UI.Components.Browser;

public partial class TopicBrowser
{
    // Apply immediately rather than wait for the refresh timer.
    private void OnUiPreferencesChanged() => _ = ApplyUiPreferencesAsync();

    private async Task ApplyUiPreferencesAsync()
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (_disposed) return;

                var changed = false;

                var multiSelect = UiSettings.Ui.TopicTreeMultiSelect;
                if (multiSelect != _multiSelect)
                {
                    _multiSelect = multiSelect;
                    if (!ForceSingleSelect)
                        _bulkSelection.SetMultiSelect(multiSelect);
                    changed = true;
                }

                var grouped = UiSettings.Ui.GroupSparkplugTopicsByNodeAndDevice;
                if (grouped != _groupSparkplugTopics)
                {
                    _groupSparkplugTopics = grouped;
                    if (!grouped)
                        _expandedPaths.RemoveWhere(TopicTreeProjection.IsVirtualExpansionId);

                    changed = true;
                }

                if (!changed) return;

                BuildVisibleRows();
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) when (_disposed) { /* component torn down before the preference was applied */ }
        catch (Exception ex) { Logger.LogWarning(ex, "TopicBrowser preference refresh failed"); }
    }
}
