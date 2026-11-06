using Microsoft.Playwright;
using MQTTnet;

namespace MqttProbe.BrowserSmokeTests;

// Bulk selection and keyboard navigation arrived on separate branches. This case proves the
// merged tree keeps its taller selection rows and still focuses the row a keyboard move asks for.
public sealed partial class MqttLiveFlowsSmokeTests
{
    private const int BulkTopicCount = 40;

    [Test]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task BulkSelectionRowsKeepTheirHeightAndKeyboardFocus(CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var root = $"bulk{Guid.NewGuid():N}";
        var connName = $"smoke-{root}";
        var clientId = $"smoke-{Guid.NewGuid():N}"[..22];
        var topics = Enumerable.Range(0, BulkTopicCount).Select(index => $"{root}/s{index:00}").ToList();
        var lastTopic = topics[^1];

        IMqttClient? mqtt = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;

        using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scenarioCts.CancelAfter(TimeSpan.FromSeconds(ScenarioBudgetSeconds));
        var token = scenarioCts.Token;

        try
        {
            mqtt = await ConnectMqttClientAsync(mqttHost, mqttPort, token);
            var (pw, br, page) = await LaunchBrowserAsync(appOrigin, token);
            playwright = pw;
            browser = br;

            await LoginAsync(page, appOrigin, username, password, token);
            await ConfigureAndConnectAsync(page, mqttHost, mqttPort, connName, clientId, token);
            await WaitForConnectedAsync(page, connName, token);

            SetStage("subscribe and publish the topic set");
            await NavigateToTab(page, "Subscriptions", token);
            await page.Locator(".subscription-editor-topic input").First
                .FillAsync($"{root}/#", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator("button[title='Add subscription']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            foreach (var topic in topics)
                await PublishAsync(mqtt, topic, $"value-{topic[^2..]}", token);

            SetStage("navigate to Browser and expand the root");
            await NavigateToTab(page, "Browser", token);
            var rootRow = page.Locator($".topic-tree-row[data-topic-path='{root}']").First;
            await rootRow.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            await rootRow.Locator("button").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            var firstRow = page.Locator($".topic-tree-row[data-topic-path='{topics[0]}']").First;
            await firstRow.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

            SetStage("measure the ordinary row height");
            var ordinary = await firstRow.BoundingBoxAsync();
            Assert.That(ordinary, Is.Not.Null, "the first topic row should have a bounding box");
            Assert.That((double)ordinary!.Height, Is.EqualTo(36).Within(0.5),
                "ordinary tree rows keep the 36px reading height");

            SetStage("enter selection mode and measure the selection row height");
            await page.Locator(".topic-select-toggle").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            var checkbox = page.Locator(
                $".topic-tree-row[data-topic-path='{topics[0]}'] .topic-select-checkbox input").First;
            await checkbox.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);

            var selection = await firstRow.BoundingBoxAsync();
            Assert.That(selection, Is.Not.Null, "the selection row should have a bounding box");
            Assert.That((double)selection!.Height, Is.EqualTo(48).Within(0.5),
                "selection rows keep the 48px touch height");

            var renderedRows = await page.Locator(".topic-tree-row").CountAsync().WaitAsync(token);
            Assert.That(renderedRows, Is.LessThan(BulkTopicCount),
                "the virtualizer should render only the rows near the viewport");

            SetStage("space on the checkbox checks exactly one topic");
            await checkbox.FocusAsync().WaitAsync(token);
            await page.Keyboard.PressAsync("Space").WaitAsync(token);
            await Assertions.Expect(page.Locator(".topic-bulk-toolbar__count").First)
                .ToHaveTextAsync("1 topic selected", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await Assertions.Expect(firstRow).ToHaveAttributeAsync(
                "aria-selected", "true", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("End scrolls to and focuses the last row");
            await firstRow.FocusAsync().WaitAsync(token);
            await page.Keyboard.PressAsync("End").WaitAsync(token);

            var treeWrap = page.Locator(".topic-tree-wrap").First;
            var wrapHandle = await treeWrap.ElementHandleAsync().WaitAsync(token);
            await page.WaitForFunctionAsync(
                "el => el.scrollTop > 0", wrapHandle, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(token);
            await page.WaitForFunctionAsync(
                "key => document.activeElement?.getAttribute('data-topic-key') === key",
                lastTopic, new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

            var lastRow = page.Locator($".topic-tree-row[data-topic-key='{lastTopic}']").First;
            await Assertions.Expect(lastRow).ToBeInViewportAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Assert.Fail($"[{_stage}] exceeded {ScenarioBudgetSeconds}s scenario budget");
        }
        finally
        {
            await CleanupAsync(mqtt, browser, playwright);
        }
    }
}
