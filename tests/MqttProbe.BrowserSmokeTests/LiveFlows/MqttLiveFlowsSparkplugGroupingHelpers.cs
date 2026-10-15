using System.Globalization;
using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

// Grouping-specific plumbing and selectors for the Sparkplug grouped-selection smoke tests.
public sealed partial class MqttLiveFlowsSmokeTests
{
    private const string SparkplugNamespace = "spBv1.0";

    private const string TopicRowSelector = ".topic-tree-row";
    private const string GroupingRowSelector = ".topic-tree-row--group";
    private const string SelectedRowSelector = ".topic-tree-row--selected";
    private const string GroupLabelSelector = ".topic-group-label";
    private const string GroupingFlagSelector = ".topic-grouping-flag";

    private const string DesktopPayloadRowSelector =
        ".browser-split-wrap:not(.browser-split-wrap--mobile) tr:has(td.pb-cell-topic)";
    private const string DetailTopicSelector = ".pb-topic";

    private const string MobileTopicNameSelector = ".mobile-topic-bar__name";
    private const string MobileCountChipSelector = ".mobile-topic-bar__count-chip";
    private const string PickerOverlaySelector = ".topic-picker-overlay";

    private const string GroupToggleLabel = "Group Sparkplug topics by node and device";

    // A grouping row's internal id is prefixed with NUL plus "spb:"; neither may reach the UI.
    private const string InternalKeyMarker = "spb:";
    private const string NullCharacter = "\0";

    private static string NodeTopic(string group, string verb, string node) =>
        $"{SparkplugNamespace}/{group}/{verb}/{node}";

    private static string DeviceTopic(string group, string verb, string node, string device) =>
        $"{SparkplugNamespace}/{group}/{verb}/{node}/{device}";

    // â”€â”€ Grouped tree rows â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static ILocator GroupRow(IPage page, string label) =>
        page.Locator($"{TopicRowSelector}:has({GroupLabelSelector}:text-is('{label}'))").First;

    private static async Task WaitForGroupRowAsync(IPage page, string label, CancellationToken token)
    {
        await GroupRow(page, label).WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
    }

    // A virtual row labels itself with .topic-group-label, but a namespace or group row is a real
    // node and renders a plain span, so match either by the text the row shows.
    private static async Task WaitForTreeRowLabelAsync(IPage page, string label, CancellationToken token)
    {
        await page.Locator($"{TopicRowSelector}:has(span:text-is('{label}'))").First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs })
            .WaitAsync(token);
    }

    // The chevron is the only expander; the row body selects.
    private static async Task ClickGroupChevronAsync(IPage page, string label, CancellationToken token)
    {
        await GroupRow(page, label).Locator("button").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task ClickRowBodyAsync(ILocator row, CancellationToken token)
    {
        await row.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task<int> TreeRowCountAsync(IPage page, CancellationToken token) =>
        await page.Locator(TopicRowSelector).CountAsync().WaitAsync(token);

    // Expand all only expands rows that already exist, so wait for the first row before
    // clicking: a click against an empty tree expands nothing and the branch stays collapsed.
    private static async Task ExpandAllTreeAsync(IPage page, CancellationToken token)
    {
        await page.Locator(TopicRowSelector).First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs })
            .WaitAsync(token);
        await page.Locator("button[title='Expand all']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // â”€â”€ Payload list â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static ILocator DesktopPayloadTopicRow(IPage page, string topic) =>
        page.Locator($".browser-split-wrap:not(.browser-split-wrap--mobile) "
            + $"tr:has(td.pb-cell-topic:text-is('{topic}'))");

    private static async Task ExpectDesktopPayloadTopicAsync(
        IPage page, string topic, CancellationToken token)
    {
        await Assertions.Expect(DesktopPayloadTopicRow(page, topic))
            .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
    }

    private static async Task ExpectNoDesktopPayloadTopicAsync(
        IPage page, string topic, CancellationToken token)
    {
        await Assertions.Expect(DesktopPayloadTopicRow(page, topic))
            .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task ExpectMobilePayloadTopicAsync(
        IPage page, string topic, CancellationToken token)
    {
        var row = page.Locator($".browser-mobile-layout tr:has(td.pb-cell-topic:text-is('{topic}'))");
        await Assertions.Expect(row).ToBeVisibleAsync(
            new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
    }

    private static async Task<int> DesktopPayloadRowCountAsync(IPage page, CancellationToken token) =>
        await page.Locator(DesktopPayloadRowSelector).CountAsync().WaitAsync(token);

    private static async Task OpenDesktopDetailAsync(IPage page, string topic, CancellationToken token)
    {
        await page.Locator($".browser-split-wrap:not(.browser-split-wrap--mobile) "
                + $"tr:has(td.pb-cell-topic:text-is('{topic}')) td.pb-cell-topic").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(page.Locator(DetailTopicSelector).First)
            .ToHaveTextAsync(topic, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task<string> DetailTopicTextAsync(IPage page, CancellationToken token) =>
        (await page.Locator(DetailTopicSelector).First.InnerTextAsync().WaitAsync(token)).Trim();

    // The detail view replaces the message list, so close it before asserting list rows again.
    private static async Task CloseDesktopDetailAsync(IPage page, CancellationToken token)
    {
        await page.Locator("button[title='Back to message list']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await Assertions.Expect(page.Locator(DetailTopicSelector))
            .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // The mobile bar refreshes on its own one-second timer, so wait for it to leave the empty
    // placeholder before reading the label.
    private static async Task<string> MobileSelectionLabelAsync(IPage page, CancellationToken token)
    {
        var name = page.Locator(MobileTopicNameSelector).First;
        await Assertions.Expect(name).Not.ToHaveTextAsync("Choose a topic",
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        return (await name.InnerTextAsync().WaitAsync(token)).Trim();
    }

    private static async Task<int?> MobileCountChipAsync(IPage page, CancellationToken token)
    {
        var chip = page.Locator(MobileCountChipSelector).First;
        if (!await chip.IsVisibleAsync().WaitAsync(token))
            return null;

        var text = (await chip.InnerTextAsync().WaitAsync(token)).Trim();
        return int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    // â”€â”€ Settings: enable grouping â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task EnsureSparkplugGroupingAsync(IPage page, CancellationToken token)
    {
        await NavigateToTab(page, "Browser", token);
        if (await page.Locator(GroupingFlagSelector).First.IsVisibleAsync().WaitAsync(token))
            return;

        SetStage("open Settings tab");
        await NavigateToTab(page, "Settings", token);

        SetStage("open Sparkplug settings section");
        await page.Locator(".settings-nav-item").Filter(new() { HasText = "Sparkplug" }).First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        SetStage("enable Sparkplug grouping");
        var field = page.Locator(".settings-field").Filter(new() { HasText = GroupToggleLabel }).First;
        await field.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);

        var toggle = field.Locator("input[type='checkbox']").First;
        if (!await toggle.IsCheckedAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token))
        {
            try
            {
                await toggle.CheckAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            }
            catch (PlaywrightException)
            {
                // MudBlazor draws a styled label around the input; clicking the label toggles it.
                await field.Locator("label, .mud-switch").First
                    .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            }
        }

        await Assertions.Expect(toggle).ToBeCheckedAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        SetStage("return to Browser with grouping on");
        await NavigateToTab(page, "Browser", token);
        await Assertions.Expect(page.Locator(GroupingFlagSelector).First)
            .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // Clears every stored topic so a grouping scenario owns the whole spBv1.0 namespace and its
    // "Devices" row cannot be confused with a leftover group from another smoke case.
    private async Task ClearAllStoredMessagesAsync(IPage page, CancellationToken token)
    {
        SetStage("clear all stored messages");
        var clear = page.Locator(".app-tabpanel-header__actions button:has-text('Clear')").First;
        if (!await clear.IsVisibleAsync().WaitAsync(token)
            || !await clear.IsEnabledAsync().WaitAsync(token))
            return;

        await clear.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var confirm = page.Locator(".mud-dialog-container button:has-text('Clear all')").First;
        await confirm.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);
        await confirm.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await confirm.WaitForAsync(
            new() { State = WaitForSelectorState.Hidden, Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private async Task SubscribeAsync(IPage page, string filter, CancellationToken token)
    {
        SetStage("subscribe to the scenario filter");
        await NavigateToTab(page, "Subscriptions", token);
        await page.Locator(".subscription-editor-topic input").First
            .FillAsync(filter, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await page.Locator("button[title='Add subscription']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // â”€â”€ Exclusion editor (used to purge one selected member) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static ILocator ExcludeEditor(IPage page) => page.Locator(".exclude-editor").First;

    // The panel renders its body only while expanded, and expansion is not preserved across
    // tab navigation, so open it when the add row is absent.
    private static async Task EnsureExcludeEditorExpandedAsync(ILocator editor, CancellationToken token)
    {
        var addRow = editor.Locator(".exclude-editor-add-row");
        if (await addRow.CountAsync().WaitAsync(token) == 0
            || !await addRow.First.IsVisibleAsync().WaitAsync(token))
        {
            await editor.Locator(".mud-expand-panel .mud-expand-panel-header").First
                .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        }
    }

    private async Task AddExcludeFilterAsync(IPage page, string filter, CancellationToken token)
    {
        SetStage("add an exclude filter to purge one selected member");
        await NavigateToTab(page, "Subscriptions", token);
        var editor = ExcludeEditor(page);
        await editor.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);
        await EnsureExcludeEditorExpandedAsync(editor, token);

        await editor.Locator(".exclude-editor-topic input").First
            .FillAsync(filter, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await editor.Locator("button[title='Add exclude topic']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(editor.Locator(".count-chip").First)
            .ToHaveTextAsync("1", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private async Task RemoveExcludeFilterAsync(IPage page, CancellationToken token)
    {
        SetStage("remove the exclude filter");
        await NavigateToTab(page, "Subscriptions", token);
        var editor = ExcludeEditor(page);
        await EnsureExcludeEditorExpandedAsync(editor, token);

        await editor.Locator(".exclude-editor-table-wrap .mud-table-body label.mud-checkbox").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await editor.Locator("button[title='Remove']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(editor.Locator(".count-chip").First)
            .ToHaveTextAsync("0", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // â”€â”€ Mobile â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static async Task ResizeToMobileAsync(IPage page, CancellationToken token)
    {
        await page.SetViewportSizeAsync(375, 667).WaitAsync(token);
        await Assertions.Expect(page.Locator(".mobile-primary-nav"))
            .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // The desktop Disconnect button is hidden at the mobile viewport, so disconnect through the
    // compact actions menu the same way the existing mobile smoke case does.
    private static async Task DisconnectAtMobileAsync(IPage page, CancellationToken token)
    {
        await page.Locator(".app-actions-compact .mud-menu").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await page.Locator(".mud-menu-item:has-text('Disconnect')").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await page.Locator(DisconnectedChip).First.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task OpenMobilePickerAndSelectAsync(
        IPage page, string groupLabel, CancellationToken token)
    {
        await page.Locator(".mobile-topic-bar__btn").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var overlay = page.Locator(PickerOverlaySelector);
        await Assertions.Expect(overlay).ToBeVisibleAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        // The picker hosts its own TopicBrowser, which starts collapsed, so reveal the group
        // hierarchy before selecting a nested grouping row.
        await overlay.Locator("button[title='Expand all']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var row = overlay.Locator($"{TopicRowSelector}:has({GroupLabelSelector}:text-is('{groupLabel}'))").First;
        await row.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
        await row.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(overlay).ToBeHiddenAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }
}
