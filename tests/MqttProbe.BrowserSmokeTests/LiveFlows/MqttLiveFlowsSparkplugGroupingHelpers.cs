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

    private static ILocator GroupRow(IPage page, string label) =>
        page.Locator($"{TopicRowSelector}:has({GroupLabelSelector}:text-is('{label}'))").First;

    private static async Task WaitForGroupRowAsync(IPage page, string label, CancellationToken token)
    {
        await GroupRow(page, label).WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
    }

    // A virtual row renders .topic-group-label, a real namespace or group row a plain span.
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

    private static ILocator NodeStatLocator(IPage page, string label) =>
        page.Locator($".browser-split-wrap:not(.browser-split-wrap--mobile):visible "
            + $"{TopicRowSelector}:visible:has({GroupLabelSelector}:text-is('{label}')) > {StatTextSelector}");

    private static async Task<string> NodeStatTextAsync(IPage page, string label, CancellationToken token)
    {
        var stat = NodeStatLocator(page, label);
        await Assertions.Expect(stat).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        return (await stat.InnerTextAsync().WaitAsync(token)).Trim();
    }

    private static async Task ExpectNodeStatChangedAsync(
        IPage page, string label, string previous, CancellationToken token) =>
        await Assertions.Expect(NodeStatLocator(page, label))
            .Not.ToHaveTextAsync(previous, new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

    private static ILocator DesktopRowByLabel(IPage page, string label) =>
        page.Locator($".browser-split-wrap:not(.browser-split-wrap--mobile):visible "
            + $"{TopicRowSelector}:visible:has(span:text-is('{label}'))");

    // Seed receipt: selecting the namespace root and awaiting each topic and payload proves the
    // store holds every endpoint before the caller expands.
    private async Task AwaitSeedReceiptAsync(
        IPage page, CancellationToken token, params (string Topic, string Payload)[] expected)
    {
        SetStage("await seed receipt");
        var namespaceRow = DesktopRowByLabel(page, SparkplugNamespace);
        await Assertions.Expect(namespaceRow).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);
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
        var stat = DesktopRowByLabel(page, SparkplugNamespace).Locator(StatTextSelector);
        await Assertions.Expect(stat).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

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

    // Expand all only expands existing rows, so wait for the first row: clicking an empty tree
    // expands nothing and the branch stays collapsed.
    private static async Task ExpandAllTreeAsync(IPage page, CancellationToken token)
    {
        await page.Locator(TopicRowSelector).First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs })
            .WaitAsync(token);
        await page.Locator("button[title='Expand all']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
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
        await page.Locator($".browser-split-wrap:not(.browser-split-wrap--mobile) "
                + $"tr:has(td.pb-cell-topic:text-is('{topic}')) td.pb-cell-topic").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        await Assertions.Expect(page.Locator(DetailTopicSelector).First)
            .ToHaveTextAsync(topic, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task<string> DetailTopicTextAsync(IPage page, CancellationToken token) =>
        (await page.Locator(DetailTopicSelector).First.InnerTextAsync().WaitAsync(token)).Trim();

    private static async Task CloseDesktopDetailAsync(IPage page, CancellationToken token)
    {
        await page.Locator("button[title='Back to message list']").First
            .ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await Assertions.Expect(page.Locator(DetailTopicSelector))
            .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // The mobile bar refreshes on its own one-second timer, so wait for the expected node before
    // reading the label: a pre-existing label from an earlier selection must not satisfy the read.
    private static async Task<string> MobileSelectionLabelAsync(
        IPage page, string expectedPart, CancellationToken token)
    {
        var name = page.Locator(MobileTopicNameSelector).First;
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
        var chip = page.Locator(MobileCountChipSelector).First;
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
