using Microsoft.Playwright;
using MQTTnet;

namespace MqttProbe.BrowserSmokeTests;

// Tests 1-3: connect/subscribe/publish, long topic overflow, status chip transitions.
public sealed partial class MqttLiveFlowsSmokeTests : LocalSmokeFixtureBase
{
    // ── Test 1: Connect, subscribe, publish, verify, disconnect ────────

    [Test]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task ConnectSubscribePublishVisibleDisconnect(CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var root = $"live{Guid.NewGuid():N}";
        var connName = $"smoke-{root}";
        var clientId = $"smoke-{Guid.NewGuid():N}"[..22];
        var subscribeFilter = $"{root}/#";
        var topic = $"{root}/sensor/temp";
        var payload = $"value-{root}";

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

            SetStage("verify connected chip");
            var chip = page.Locator(ConnectedChip).First;
            await chip.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs })
                .WaitAsync(token);
            await Assertions.Expect(chip).ToHaveTextAsync(connName, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(token);

            SetStage("wait for Pause button");
            await page.Locator(PauseButton).First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("navigate to Subscriptions tab");
            await NavigateToTab(page, "Subscriptions", token);

            SetStage("add subscription");
            await page.Locator(".subscription-editor-topic input").First
                .FillAsync(subscribeFilter, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator("button[title='Add subscription']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("publish message");
            await PublishAsync(mqtt, topic, payload, token);

            SetStage("navigate to Browser tab");
            await NavigateToTab(page, "Browser", token);

            SetStage("expand topic tree");
            await page.Locator($".topic-tree-row:has-text('{root}')").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("verify message in payload table");
            await Assertions.Expect(PayloadRow(page, topic, payload))
                .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

            SetStage("disconnect via UI");
            await page.Locator(DisconnectButton).ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("verify disconnected chip");
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

    // ── Test 2: Long topic string overflow ─────────────────────────────

    [Test]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task LongTopicStringOverflowAndScroll(CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var root = $"long{Guid.NewGuid():N}";
        var connName = $"smoke-{root}";
        var clientId = $"smoke-{Guid.NewGuid():N}"[..22];
        // 210-char segment to stress layout
        var longSegment = string.Join("", Enumerable.Repeat("segment", 30));
        var longTopic = $"{root}/{longSegment}/data";
        var payload = "long-topic-payload";

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

            SetStage("subscribe and publish long topic");
            await NavigateToTab(page, "Subscriptions", token);
            await page.Locator(".subscription-editor-topic input").First
                .FillAsync($"{root}/#", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator("button[title='Add subscription']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await PublishAsync(mqtt, longTopic, payload, token);

            SetStage("navigate to Browser");
            await NavigateToTab(page, "Browser", token);

            SetStage("expand root node via chevron");
            var rootNode = page.Locator($".topic-tree-row:has-text('{root}')").First;
            await rootNode.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            await rootNode.Locator("button").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("find the actual long-segment tree row");
            var longPrefix = longSegment[..30];
            var longRow = page.Locator($".topic-tree-row:has-text('{longPrefix}')").First;
            await longRow.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

            var rowBox = await longRow.BoundingBoxAsync();
            Assert.That(rowBox, Is.Not.Null, "long topic tree row should have a bounding box");

            SetStage("assert no horizontal overflow in topic tree scroll region");
            var treeWrap = page.Locator(".topic-tree-wrap").First;
            var hasTreeOverflow = await treeWrap.EvaluateAsync<bool>(
                "el => el.scrollWidth > el.clientWidth");
            Assert.That(hasTreeOverflow, Is.False,
                $"topic tree scrollWidth should not exceed clientWidth (row width={rowBox!.Width}px)");

            SetStage("expand long segment node via chevron");
            await longRow.Locator("button").First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("select data leaf and verify payload");
            var dataLeaf = page.Locator($".topic-tree-row:has-text('data')").First;
            await dataLeaf.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            await dataLeaf.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("assert page has no horizontal scrollbar");
            var pageOverflow = await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
            Assert.That(pageOverflow, Is.False,
                "page should not have horizontal overflow from long topic");

            SetStage("verify payload row for long topic");
            await Assertions.Expect(PayloadRow(page, longTopic, payload))
                .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

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

    // ── Test 3: Status chip transitions ────────────────────────────────

    [Test]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task StatusChipTransitionsDisconnectReconnect(CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var root = $"chip{Guid.NewGuid():N}";
        var connName = $"smoke-{root}";
        var clientId = $"smoke-{Guid.NewGuid():N}"[..22];

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

            // ── First connection ───────────────────────────────────────
            SetStage("first connect");
            await ConfigureAndConnectAsync(page, mqttHost, mqttPort, connName, clientId, token);
            await WaitForConnectedAsync(page, connName, token);

            // ── Disconnect via UI ──────────────────────────────────────
            SetStage("disconnect via UI");
            await page.Locator(DisconnectButton).ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("verify disconnected chip");
            await page.Locator(DisconnectedChip).First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);

            // ── Reconnect: open dialog and connect again ───────────────
            SetStage("reopen connection dialog");
            await page.Locator(ConnectButton).First.ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("wait for connection dialog");
            var dialog = page.Locator(ConnectionDialog).First;
            await dialog.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);

            // The previous connection should be preselected. Click Connect.
            SetStage("click Connect for reconnect");
            await dialog.Locator("[title='Connect']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            // Handle "Connect anyway" prompt if it appears
            var promptBtn = page.Locator(".mud-dialog-container button:has-text('Connect anyway')").First;
            var connectDeadline = DateTime.UtcNow.AddMilliseconds(NavTimeoutMs);
            while (DateTime.UtcNow < connectDeadline)
            {
                if (await page.Locator(ConnectedChip).First.IsVisibleAsync().WaitAsync(token)) break;
                if (await promptBtn.IsVisibleAsync().WaitAsync(token))
                {
                    await promptBtn.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
                    break;
                }
                await Task.Delay(250, token);
            }

            SetStage("verify reconnected chip");
            await page.Locator(ConnectedChip).First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs }).WaitAsync(token);

            SetStage("disconnect again");
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
}
