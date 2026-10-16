using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

public sealed partial class MqttLiveFlowsSmokeTests
{
    private static ILocator DesktopTreeRegion(IPage page) =>
        page.Locator(".browser-split-wrap:not(.browser-split-wrap--mobile):visible");

    private static ILocator PickerRegion(IPage page) => page.Locator($"{PickerOverlaySelector}:visible");

    private static async Task<ILocator> UniqueAsync(ILocator locator, CancellationToken token)
    {
        await Assertions.Expect(locator).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        return locator;
    }

    private static ILocator DesktopVirtualRow(IPage page, string label) =>
        DesktopTreeRegion(page).Locator($"{TopicRowSelector}:visible:has({GroupLabelSelector}:text-is('{label}'))");

    private static ILocator DesktopRowByLabel(IPage page, string label) =>
        DesktopTreeRegion(page).Locator($"{TopicRowSelector}:visible:has(span:text-is('{label}'))");

    private static ILocator DesktopNodeStatLocator(IPage page, string label) =>
        DesktopVirtualRow(page, label).Locator(StatTextSelector);

    private static async Task WaitForGroupRowAsync(IPage page, string label, CancellationToken token)
    {
        var row = await UniqueAsync(DesktopVirtualRow(page, label), token);
        await Assertions.Expect(row).ToBeVisibleAsync(
            new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
    }

    private static async Task WaitForTreeRowLabelAsync(IPage page, string label, CancellationToken token)
    {
        var row = await UniqueAsync(DesktopRowByLabel(page, label), token);
        await Assertions.Expect(row).ToBeVisibleAsync(
            new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
    }

    private static async Task ClickGroupChevronAsync(IPage page, string label, CancellationToken token)
    {
        var chevron = await UniqueAsync(DesktopVirtualRow(page, label).Locator("button"), token);
        await chevron.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task ClickRowBodyAsync(ILocator row, CancellationToken token)
    {
        var target = await UniqueAsync(row, token);
        await target.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private static async Task<int> TreeRowCountAsync(IPage page, CancellationToken token) =>
        await DesktopTreeRegion(page).Locator(TopicRowSelector).CountAsync().WaitAsync(token);

    private static async Task<string> NodeStatTextAsync(IPage page, string label, CancellationToken token)
    {
        var stat = await UniqueAsync(DesktopNodeStatLocator(page, label), token);
        return (await stat.InnerTextAsync().WaitAsync(token)).Trim();
    }

    private static async Task ExpectNodeStatChangedAsync(
        IPage page, string label, string previous, CancellationToken token) =>
        await Assertions.Expect(DesktopNodeStatLocator(page, label))
            .Not.ToHaveTextAsync(previous, new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

    private static async Task WaitForDisconnectedAsync(IPage page, CancellationToken token)
    {
        var chip = await UniqueAsync(page.Locator(DisconnectedChip), token);
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }
}
