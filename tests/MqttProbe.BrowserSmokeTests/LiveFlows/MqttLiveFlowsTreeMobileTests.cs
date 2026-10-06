using Microsoft.Playwright;
using MQTTnet;

namespace MqttProbe.BrowserSmokeTests;

// Tests 4-5: topic tree expand/collapse and mobile viewport with live broker.
public sealed partial class MqttLiveFlowsSmokeTests
{
    // ── Test 4: Topic tree expand/collapse with multiple topics ────────

    [Test]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task TopicTreeExpandCollapseMultipleTopics(CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var root = $"tree{Guid.NewGuid():N}";
        var connName = $"smoke-{root}";
        var clientId = $"smoke-{Guid.NewGuid():N}"[..22];

        var topics = new[]
        {
            $"{root}/sensors/temp",
            $"{root}/sensors/humidity",
            $"{root}/actuators/valve",
        };
        var payloads = topics.Select((_, i) => $"payload-{i}").ToArray();

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

            SetStage("subscribe and publish multiple topics");
            await NavigateToTab(page, "Subscriptions", token);
            await page.Locator(".subscription-editor-topic input").First
                .FillAsync($"{root}/#", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator("button[title='Add subscription']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            for (var i = 0; i < topics.Length; i++)
                await PublishAsync(mqtt, topics[i], payloads[i], token);

            SetStage("navigate to Browser");
            await NavigateToTab(page, "Browser", token);

            SetStage("expand root node via chevron");
            var rootNode = page.Locator($".topic-tree-row:has-text('{root}')").First;
            await rootNode.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            await rootNode.Locator("button").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("assert child nodes visible after expand");
            var sensorsNode = page.Locator(".topic-tree-row:has-text('sensors')").First;
            var actuatorsNode = page.Locator(".topic-tree-row:has-text('actuators')").First;
            await Assertions.Expect(sensorsNode).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await Assertions.Expect(actuatorsNode).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("expand sensors node via chevron");
            await sensorsNode.Locator("button").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("assert leaf nodes visible");
            var tempNode = page.Locator($".topic-tree-row:has-text('temp')").First;
            var humidityNode = page.Locator($".topic-tree-row:has-text('humidity')").First;
            await Assertions.Expect(tempNode).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await Assertions.Expect(humidityNode).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("select root and verify all messages visible");
            await rootNode.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            for (var i = 0; i < topics.Length; i++)
            {
                await Assertions.Expect(PayloadRow(page, topics[i], payloads[i]))
                    .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            }

            SetStage("collapse root node via chevron");
            await rootNode.Locator("button").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("assert child nodes hidden after collapse");
            await Assertions.Expect(page.Locator(".topic-tree-row:has-text('sensors')"))
                .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await Assertions.Expect(page.Locator(".topic-tree-row:has-text('actuators')"))
                .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("re-expand root node via chevron");
            await rootNode.Locator("button").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("assert child nodes visible after re-expand");
            await Assertions.Expect(page.Locator(".topic-tree-row:has-text('sensors')").First)
                .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await Assertions.Expect(page.Locator(".topic-tree-row:has-text('actuators')").First)
                .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("select root and verify messages return after re-expand");
            await rootNode.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            for (var i = 0; i < topics.Length; i++)
            {
                await Assertions.Expect(PayloadRow(page, topics[i], payloads[i]))
                    .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            }

            SetStage("disconnect");
            await page.Locator(DisconnectButton).ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator(DisconnectedChip).First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);
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

    // ── Test 5: Mobile viewport with live broker ───────────────────────

    [Test]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task MobileViewportWithLiveBroker(CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var root = $"mobile{Guid.NewGuid():N}";
        var connName = $"smoke-{root}";
        var clientId = $"smoke-{Guid.NewGuid():N}"[..22];
        var topic = $"{root}/test";
        var payload = "mobile-payload";

        IMqttClient? mqtt = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;

        using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scenarioCts.CancelAfter(TimeSpan.FromSeconds(ScenarioBudgetSeconds));
        var token = scenarioCts.Token;

        try
        {
            mqtt = await ConnectMqttClientAsync(mqttHost, mqttPort, token);
            playwright = await Playwright.CreateAsync().WaitAsync(token);
            browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }).WaitAsync(token);
            var context = await browser.NewContextAsync(new()
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new() { Width = 375, Height = 667 },
            }).WaitAsync(token);
            var page = await context.NewPageAsync().WaitAsync(token);
            page.SetDefaultTimeout(ActionTimeoutMs);
            page.SetDefaultNavigationTimeout(NavTimeoutMs);

            SetStage("navigate to /Login");
            await page.GotoAsync($"{appOrigin}/Login", new()
            {
                Timeout = NavTimeoutMs,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            }).WaitAsync(token);

            await LoginAsync(page, appOrigin, username, password, token);
            await ConfigureAndConnectAsync(page, mqttHost, mqttPort, connName, clientId, token);
            await WaitForConnectedAsync(page, connName, token, mobile: true);

            SetStage("assert mobile nav visible");
            var mobileNav = page.Locator(".mobile-primary-nav");
            await Assertions.Expect(mobileNav).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("assert desktop tabbar hidden");
            var desktopTabbar = page.Locator(".desktop-tabs .mud-tabs-tabbar");
            Assert.That(await desktopTabbar.IsVisibleAsync().WaitAsync(token), Is.False,
                "desktop tabbar should be hidden at mobile viewport");

            SetStage("assert compact actions visible");
            var compactActions = page.Locator(".app-actions-compact");
            Assert.That(await compactActions.IsVisibleAsync().WaitAsync(token), Is.True,
                "compact actions should be visible at mobile viewport");

            SetStage("navigate to Subscriptions via More menu");
            var moreMenu = page.Locator(".mobile-nav-more");
            await Assertions.Expect(moreMenu).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await moreMenu.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            var subsItem = page.Locator(".mud-menu-item:has-text('Subscriptions')");
            await Assertions.Expect(subsItem).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await subsItem.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("add subscription at mobile");
            var topicInput = page.Locator(".subscription-editor-topic input").First;
            await Assertions.Expect(topicInput).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await topicInput.FillAsync($"{root}/#", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator("button[title='Add subscription']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("publish via external MQTT");
            await PublishAsync(mqtt, topic, payload, token);

            SetStage("navigate to Browser via mobile nav");
            var browserBtn = page.Locator(".mobile-nav-btn:has-text('Browser')");
            await Assertions.Expect(browserBtn).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await browserBtn.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("open topic picker on mobile");
            var topicsBtn = page.Locator(".mobile-topic-bar__btn");
            await Assertions.Expect(topicsBtn).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await topicsBtn.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("select root topic in picker overlay");
            var pickerOverlay = page.Locator(".topic-picker-overlay");
            await Assertions.Expect(pickerOverlay).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            var pickerRootRow = pickerOverlay.Locator($".topic-tree-row:has-text('{root}')").First;
            await Assertions.Expect(pickerRootRow).ToBeVisibleAsync(
                new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            await pickerRootRow.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("verify picker overlay closed");
            await Assertions.Expect(pickerOverlay).ToBeHiddenAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("verify message in payload table");
            await Assertions.Expect(PayloadRow(page, topic, payload))
                .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

            SetStage("disconnect at mobile via compact menu");
            var compactMore = page.Locator(".app-actions-compact .mud-menu");
            await Assertions.Expect(compactMore).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await compactMore.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            var disconnectItem = page.Locator(".mud-menu-item:has-text('Disconnect')");
            await Assertions.Expect(disconnectItem).ToBeVisibleAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await disconnectItem.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("verify disconnected chip at mobile");
            await Assertions.Expect(page.Locator(DisconnectedChip).First).ToBeVisibleAsync(
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
