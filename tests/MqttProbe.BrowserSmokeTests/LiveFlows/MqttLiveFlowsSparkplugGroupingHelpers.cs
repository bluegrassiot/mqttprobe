using System.Globalization;
using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

public sealed partial class MqttLiveFlowsSmokeTests
{
    private const string SparkplugNamespace = "spBv1.0";

    private const string TopicRowSelector = ".topic-tree-row";
    private const string GroupingRowSelector = ".topic-tree-row--group";
    private const string SelectedRowSelector = ".topic-tree-row--selected";
    private const string GroupLabelSelector = ".topic-group-label";
    private const string StatTextSelector = ".topic-stat-text";
    private const string GroupingFlagSelector = ".topic-grouping-flag";

    private const string DesktopPayloadRowSelector =
        ".browser-split-wrap:not(.browser-split-wrap--mobile) tr:has(td.pb-cell-topic)";
    private const string DetailTopicSelector = ".pb-topic";

    private const string MobileTopicNameSelector = ".mobile-topic-bar__name";
    private const string MobileCountChipSelector = ".mobile-topic-bar__count-chip";
    private const string PickerOverlaySelector = ".topic-picker-overlay";

    private const int StabilityWindowMs = 1_500;
    private const int StabilitySampleIntervalMs = 100;

    private const string GroupToggleLabel = "Group Sparkplug topics by node and device";

    // A grouping row's internal id is prefixed with NUL plus "spb:"; neither may reach the UI.
    private const string InternalKeyMarker = "spb:";
    private const string NullCharacter = "\0";

    private static string NodeTopic(string group, string verb, string node) =>
        $"{SparkplugNamespace}/{group}/{verb}/{node}";

    private static string DeviceTopic(string group, string verb, string node, string device) =>
        $"{SparkplugNamespace}/{group}/{verb}/{node}/{device}";

    // Seed receipt: selecting the namespace root and awaiting each topic and payload proves the
    // store holds every endpoint before the caller expands.
    private async Task AwaitSeedReceiptAsync(
        IPage page, CancellationToken token, params (string Topic, string Payload)[] expected)
    {
        SetStage("await seed receipt");
        var namespaceRow = await UniqueAsync(DesktopRowByLabel(page, SparkplugNamespace), token);
        await namespaceRow.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        foreach (var (topic, payload) in expected)
        {
            await Assertions.Expect(PayloadRow(page, topic, payload))
                .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
        }
    }

    // Receipt for steps that must not select: the namespace row's message count only grows, so
    // reaching the expected total proves every seed message is stored.
    private async Task AwaitNamespaceMessageCountAsync(IPage page, int expectedMessages, CancellationToken token)
    {
        SetStage("await namespace message count");
        var stat = await UniqueAsync(
            DesktopRowByLabel(page, SparkplugNamespace).Locator(StatTextSelector), token);

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(MessageWaitTimeoutMs);
        while (true)
        {
            var text = (await stat.InnerTextAsync().WaitAsync(token)).Trim();
            if (MessageCountFrom(text) == expectedMessages)
                return;

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("namespace message count did not reach "
                    + expectedMessages.ToString(CultureInfo.InvariantCulture) + $": '{text}'");
            }

            await Task.Delay(StabilitySampleIntervalMs, token);
        }
    }

    private static int? MessageCountFrom(string statText)
    {
        var marker = statText.IndexOf("M ", StringComparison.Ordinal);
        if (marker < 0)
            return null;

        var digits = new string(statText[(marker + 2)..].Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // This observes stability, not a server timer tick.
    private static async Task ObserveNoSelectionAndEmptyListStableAsync(
        IPage page, TimeSpan window, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (await page.Locator(SelectedRowSelector).CountAsync().WaitAsync(token) > 0)
                Assert.Fail("a topic-tree row became selected during the stability window");

            if (await page.Locator(DesktopPayloadRowSelector).CountAsync().WaitAsync(token) > 0)
                Assert.Fail("a payload row appeared during the stability window");

            if (DateTime.UtcNow >= deadline)
                return;

            await Task.Delay(StabilitySampleIntervalMs, token);
        }
    }

    // Expand all snapshots the store, so the seed receipt has already proved every endpoint is stored.
    private static async Task ExpandAllTreeAsync(IPage page, CancellationToken token)
    {
        var expandAll = await UniqueAsync(
            DesktopTreeRegion(page).Locator("button[title='Expand all']"), token);
        await expandAll.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

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
        var cell = await UniqueAsync(page.Locator($".browser-split-wrap:not(.browser-split-wrap--mobile) "
            + $"tr:has(td.pb-cell-topic:text-is('{topic}')) td.pb-cell-topic"), token);
        await cell.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var detail = await UniqueAsync(page.Locator(DetailTopicSelector), token);
        await Assertions.Expect(detail).ToHaveTextAsync(topic, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);
    }

    private static async Task<string> DetailTopicTextAsync(IPage page, CancellationToken token)
    {
        var detail = await UniqueAsync(page.Locator(DetailTopicSelector), token);
        return (await detail.InnerTextAsync().WaitAsync(token)).Trim();
    }

    private static async Task CloseDesktopDetailAsync(IPage page, CancellationToken token)
    {
        var back = await UniqueAsync(page.Locator("button[title='Back to message list']"), token);
        await back.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await Assertions.Expect(page.Locator(DetailTopicSelector))
            .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // The mobile bar refreshes on its own one-second timer, so wait for the expected node before
    // reading the label: a pre-existing label from an earlier selection must not satisfy the read.
    private static async Task<string> MobileSelectionLabelAsync(
        IPage page, string expectedPart, CancellationToken token)
    {
        var name = await UniqueAsync(page.Locator(MobileTopicNameSelector), token);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ActionTimeoutMs);
        while (true)
        {
            var text = (await name.InnerTextAsync().WaitAsync(token)).Trim();
            if (text.Contains(expectedPart, StringComparison.Ordinal))
                return text;

            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"mobile selection label never showed '{expectedPart}': '{text}'");

            await Task.Delay(StabilitySampleIntervalMs, token);
        }
    }

    private static async Task<int?> MobileCountChipAsync(IPage page, CancellationToken token)
    {
        var chips = page.Locator(MobileCountChipSelector);
        if (await chips.CountAsync().WaitAsync(token) == 0)
            return null;

        var chip = await UniqueAsync(chips, token);
        if (!await chip.IsVisibleAsync().WaitAsync(token))
            return null;

        var text = (await chip.InnerTextAsync().WaitAsync(token)).Trim();
        return int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private async Task EnsureSparkplugGroupingAsync(IPage page, CancellationToken token)
    {
        await NavigateToTab(page, "Browser", token);
        var flag = page.Locator(GroupingFlagSelector);
        if (await flag.CountAsync().WaitAsync(token) == 1
            && await flag.IsVisibleAsync().WaitAsync(token))
            return;

        SetStage("open Settings tab");
        await NavigateToTab(page, "Settings", token);

        SetStage("open Sparkplug settings section");
        var settingsNav = await UniqueAsync(
            page.Locator(".settings-nav-item").Filter(new() { HasText = "Sparkplug" }), token);
        await settingsNav.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        SetStage("enable Sparkplug grouping");
        var field = await UniqueAsync(
            page.Locator(".settings-field").Filter(new() { HasText = GroupToggleLabel }), token);
        await Assertions.Expect(field).ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var toggle = await UniqueAsync(field.Locator("input[type='checkbox']"), token);
        if (!await toggle.IsCheckedAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token))
        {
            try
            {
                await toggle.CheckAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            }
            catch (PlaywrightException)
            {
                // MudBlazor draws a styled label around the input; clicking the label toggles it.
                var label = await UniqueAsync(field.Locator("label, .mud-switch"), token);
                await label.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            }
        }

        await Assertions.Expect(toggle).ToBeCheckedAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        SetStage("return to Browser with grouping on");
        await NavigateToTab(page, "Browser", token);
        await Assertions.Expect(await UniqueAsync(page.Locator(GroupingFlagSelector), token))
            .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // Clears every stored topic so a grouping scenario owns the whole spBv1.0 namespace and its
    // "Devices" row cannot be confused with a leftover group from another smoke case.
    private async Task ClearAllStoredMessagesAsync(IPage page, CancellationToken token)
    {
        SetStage("clear all stored messages");
        var clearButtons = page.Locator(".app-tabpanel-header__actions button:has-text('Clear')");
        if (await clearButtons.CountAsync().WaitAsync(token) == 0)
            return;

        var clear = await UniqueAsync(clearButtons, token);
        if (!await clear.IsVisibleAsync().WaitAsync(token)
            || !await clear.IsEnabledAsync().WaitAsync(token))
            return;

        await clear.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var confirm = await UniqueAsync(page.Locator(".mud-dialog-container button:has-text('Clear all')"), token);
        await Assertions.Expect(confirm).ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await confirm.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await Assertions.Expect(confirm).ToBeHiddenAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private async Task SubscribeAsync(IPage page, string filter, CancellationToken token)
    {
        SetStage("subscribe to the scenario filter");
        await NavigateToTab(page, "Subscriptions", token);
        var topicInput = await UniqueAsync(page.Locator(".subscription-editor-topic input"), token);
        await topicInput.FillAsync(filter, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var add = await UniqueAsync(page.Locator("button[title='Add subscription']"), token);
        await add.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // The panel renders its body only while expanded, and expansion is not preserved across
    // tab navigation, so open it when the add row is absent.
    private static async Task EnsureExcludeEditorExpandedAsync(ILocator editor, CancellationToken token)
    {
        var addRows = editor.Locator(".exclude-editor-add-row");
        if (await addRows.CountAsync().WaitAsync(token) == 0)
        {
            await ClickExcludePanelHeaderAsync(editor, token);
            return;
        }

        var addRow = await UniqueAsync(addRows, token);
        if (!await addRow.IsVisibleAsync().WaitAsync(token))
            await ClickExcludePanelHeaderAsync(editor, token);
    }

    private static async Task ClickExcludePanelHeaderAsync(ILocator editor, CancellationToken token)
    {
        var header = await UniqueAsync(editor.Locator(".mud-expand-panel .mud-expand-panel-header"), token);
        await header.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private async Task AddExcludeFilterAsync(IPage page, string filter, CancellationToken token)
    {
        SetStage("add an exclude filter to purge one selected member");
        await NavigateToTab(page, "Subscriptions", token);
        var editor = await UniqueAsync(page.Locator(".exclude-editor"), token);
        await EnsureExcludeEditorExpandedAsync(editor, token);

        var topicInput = await UniqueAsync(editor.Locator(".exclude-editor-topic input"), token);
        await topicInput.FillAsync(filter, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var add = await UniqueAsync(editor.Locator("button[title='Add exclude topic']"), token);
        await add.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(await UniqueAsync(editor.Locator(".count-chip"), token))
            .ToHaveTextAsync("1", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private async Task RemoveExcludeFilterAsync(IPage page, CancellationToken token)
    {
        SetStage("remove the exclude filter");
        await NavigateToTab(page, "Subscriptions", token);
        var editor = await UniqueAsync(page.Locator(".exclude-editor"), token);
        await EnsureExcludeEditorExpandedAsync(editor, token);

        var checkbox = await UniqueAsync(
            editor.Locator(".exclude-editor-table-wrap .mud-table-body label.mud-checkbox"), token);
        await checkbox.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var remove = await UniqueAsync(editor.Locator("button[title='Remove']"), token);
        await remove.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(await UniqueAsync(editor.Locator(".count-chip"), token))
            .ToHaveTextAsync("0", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

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
        var menu = await UniqueAsync(page.Locator(".app-actions-compact .mud-menu"), token);
        await menu.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var disconnect = await UniqueAsync(page.Locator(".mud-menu-item:has-text('Disconnect')"), token);
        await disconnect.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await WaitForDisconnectedAsync(page, token);
    }

    private static async Task OpenMobilePickerAndSelectAsync(
        IPage page, string groupLabel, CancellationToken token)
    {
        var topicsButton = await UniqueAsync(page.Locator(".mobile-topic-bar__btn"), token);
        await topicsButton.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var overlay = await UniqueAsync(PickerRegion(page), token);

        // The picker hosts its own TopicBrowser, which starts collapsed, so reveal the group
        // hierarchy before selecting a nested grouping row.
        var pickerExpandAll = await UniqueAsync(overlay.Locator("button[title='Expand all']"), token);
        await pickerExpandAll.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var row = await UniqueAsync(
            overlay.Locator($"{TopicRowSelector}:visible:has({GroupLabelSelector}:text-is('{groupLabel}'))"), token);
        await Assertions.Expect(row).ToBeVisibleAsync(
            new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
        await row.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(overlay).ToBeHiddenAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }
}
