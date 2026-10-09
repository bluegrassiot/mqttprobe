using Microsoft.Playwright;
using MQTTnet;

namespace MqttProbe.BrowserSmokeTests;

public sealed partial class MqttLiveFlowsSmokeTests
{
    private static IEnumerable<TestCaseData> EmulatorCases()
    {
        yield return new TestCaseData(false).SetName("GenericEmulatorPublishesVisibleData");
        yield return new TestCaseData(true).SetName("SparkplugEmulatorPublishesDecodedDataAndStateText");
    }

    [TestCaseSource(nameof(EmulatorCases))]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public Task EmulatorPublishesAndStops(bool sparkplug, CancellationToken cancellationToken) =>
        RunEmulatorAsync(sparkplug, cancellationToken);

    private async Task RunEmulatorAsync(bool sparkplug, CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");
        var suffix = Guid.NewGuid().ToString("N");
        var group = $"smoke{suffix[..8]}";
        var node = $"Node-{suffix[..6]}";
        var connName = $"smoke-{suffix}";
        var clientId = $"smoke-{suffix}"[..22];
        var subscription = sparkplug ? "spBv1.0/#" : $"{group}/#";
        var stateHost = $"smoke-{suffix[..8]}";

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

            SetStage("subscribe to emulator output");
            await NavigateToTab(page, "Subscriptions", token);
            await page.Locator(".subscription-editor-topic input").First.FillAsync(subscription,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator("button[title='Add subscription']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await page.Locator(
                    $".subscription-editor-table-wrap .mud-table-row:has(td:text-is('{subscription}'))")
                .First.WaitForAsync(new()
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = SubscribeSettleTimeoutMs,
                }).WaitAsync(token);

            var emulationTab = page.GetByRole(AriaRole.Tab).Filter(new() { HasText = "Emulation" });
            SetStage("configure emulator: open Emulation tab");
            await emulationTab.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            var nodeCards = page.Locator(".emu-node-row");
            SetStage("configure emulator: count existing node cards");
            var nodeCardCountBeforeAdd = await nodeCards.CountAsync().WaitAsync(token);
            SetStage("configure emulator: capture existing node identities");
            var existingNodeIdentities = await nodeCards.EvaluateAllAsync<string[]>(
                "cards => cards.map(card => `${card.querySelector('.emu-node-id')?.textContent?.trim()}\\u001f${card.querySelector('.emu-node-secondary')?.textContent?.trim()}`)")
                .WaitAsync(token);
            Assert.That(existingNodeIdentities, Has.Length.EqualTo(nodeCardCountBeforeAdd),
                "each existing emulator card should expose a node ID and summary");

            SetStage("configure emulator: add node");
            await page.Locator("button[title='Add node']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("configure emulator: wait for added node card");
            await Assertions.Expect(nodeCards).ToHaveCountAsync(nodeCardCountBeforeAdd + 1,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("configure emulator: capture node identities after add");
            var nodeIdentitiesAfterAdd = await nodeCards.EvaluateAllAsync<string[]>(
                "cards => cards.map(card => `${card.querySelector('.emu-node-id')?.textContent?.trim()}\\u001f${card.querySelector('.emu-node-secondary')?.textContent?.trim()}`)")
                .WaitAsync(token);
            var newCardIndexes = Enumerable.Range(0, nodeIdentitiesAfterAdd.Length)
                .Where(index => !existingNodeIdentities.Contains(nodeIdentitiesAfterAdd[index],
                    StringComparer.Ordinal))
                .ToArray();
            SetStage("configure emulator: assert one new node identity");
            Assert.That(newCardIndexes, Has.Length.EqualTo(1),
                "adding a node should introduce exactly one new node identity");

            var addedCard = nodeCards.Nth(newCardIndexes[0]);
            SetStage("configure emulator: read added node ID");
            var defaultNodeId = (await addedCard.Locator(".emu-node-id").InnerTextAsync()
                .WaitAsync(token)).Trim();
            SetStage("configure emulator: read added node summary");
            var defaultNodeSummary = (await addedCard.Locator(".emu-node-secondary").InnerTextAsync()
                .WaitAsync(token)).Trim();
            var nodeCard = page.Locator(
                $".emu-node-row:has(.emu-node-id:text-is('{defaultNodeId}'))"
                + $":has(.emu-node-secondary:text-is('{defaultNodeSummary}'))");
            ILocator FindNodeCard(string groupId, string nodeId) => page.Locator(
                $".emu-node-row:has(.emu-node-id:text-is('{nodeId}'))"
                + $":has(.emu-node-secondary:has-text('{groupId}'))");

            SetStage("configure emulator: identify added card");
            await Assertions.Expect(nodeCard).ToHaveCountAsync(1,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            var groupIdField = nodeCard.GetByLabel("Group ID", new() { Exact = true });
            SetStage("configure emulator: fill generated Group ID");
            await groupIdField.FillAsync(group,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("configure emulator: commit Group ID");
            await groupIdField.PressAsync("Tab", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            nodeCard = FindNodeCard(group, defaultNodeId);
            SetStage("configure emulator: reidentify card after Group ID commit");
            await Assertions.Expect(nodeCard).ToHaveCountAsync(1,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            var nodeIdField = nodeCard.GetByLabel("Node ID", new() { Exact = true });
            SetStage("configure emulator: fill generated Node ID");
            await nodeIdField.FillAsync(node,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("configure emulator: commit Node ID");
            await nodeIdField.PressAsync("Tab", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            nodeCard = FindNodeCard(group, node);
            SetStage("configure emulator: reidentify card after Node ID commit");
            await Assertions.Expect(nodeCard).ToHaveCountAsync(1,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            if (!sparkplug)
            {
                var typeControl = nodeCard.Locator("[role='combobox'][aria-label='Type']:visible");
                SetStage("configure emulator: identify visible Type control");
                await Assertions.Expect(typeControl).ToHaveCountAsync(1,
                    new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
                SetStage("configure emulator: open Type selector");
                await typeControl.ClickAsync(
                    new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
                SetStage("configure emulator: select Generic MQTT type");
                await page.GetByText("Generic MQTT", new() { Exact = true }).ClickAsync(
                    new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
                SetStage("configure emulator: verify Generic type");
                await Assertions.Expect(nodeCard).ToContainTextAsync("Generic",
                    new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            }

            SetStage("start emulator: click Start");
            await page.Locator("button[title='Start emulation']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("start emulator: wait for Stop button");
            await page.Locator("button[title='Stop emulation']").WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs }).WaitAsync(token);
            SetStage("start emulator: verify running count");
            await Assertions.Expect(page.Locator(".emu-count-chip--running"))
                .ToContainTextAsync("Running", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            if (sparkplug)
            {
                await VerifySparkplugEmulatorAsync(page, mqtt, group, node, stateHost, token);
            }
            else
            {
                await VerifyGenericEmulatorAsync(page, group, node, token);
            }

            SetStage("stop emulator: return to Emulation tab");
            await emulationTab.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("stop emulator: click Stop");
            await page.Locator("button[title='Stop emulation']").ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("stop emulator: wait for Start button");
            await page.Locator("button[title='Start emulation']").WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("stop emulator: verify no running count");
            await Assertions.Expect(page.Locator(".emu-count-chip--running"))
                .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

            SetStage("disconnect: click Disconnect");
            await page.Locator(DisconnectButton).ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            SetStage("disconnect: wait for disconnected status");
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

    private async Task VerifyGenericEmulatorAsync(
        IPage page, string group, string node, CancellationToken token)
    {
        SetStage("verify Generic MQTT: navigate to Browser tab");
        await NavigateToTab(page, "Browser", token);
        var root = page.Locator($".topic-tree-row:has-text('{group}')").First;
        SetStage("verify Generic MQTT: wait for generated group row");
        await root.WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = MessageWaitTimeoutMs,
        }).WaitAsync(token);
        SetStage("verify Generic MQTT: select generated group row");
        await root.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var topic = $"{group}/{node}/Device-1";
        var message = page.Locator(
            $".browser-split-wrap:not(.browser-split-wrap--mobile) .mud-table-body tr:has(td.pb-cell-topic:text-is('{topic}'))");
        SetStage("verify Generic MQTT: wait for generated topic row");
        await Assertions.Expect(message).Not.ToHaveCountAsync(0,
            new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
        SetStage("verify Generic MQTT: read generated payloads");
        var payloads = await message.Locator("td.pb-cell-payload").AllInnerTextsAsync()
            .WaitAsync(token);
        SetStage("verify Generic MQTT: assert configured metric payload");
        Assert.That(payloads, Has.Some.Contains("Metric-1"),
            "the Generic JSON payload should expose the configured metric");

        SetStage("verify Generic MQTT: fill topic filter");
        var topicFilter = page.Locator(".topic-filter-field input");
        await topicFilter.FillAsync(node, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("verify Generic MQTT: wait for filtered node row");
        await Assertions.Expect(page.Locator($".topic-tree-row:has-text('{node}')").First)
            .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("verify Generic MQTT: fill unmatched topic filter");
        await topicFilter.FillAsync($"missing-{node}",
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("verify Generic MQTT: assert unmatched filter message");
        await Assertions.Expect(page.Locator(".topic-tree-wrap"))
            .ToContainTextAsync("No topics match", new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("verify Generic MQTT: clear topic filter");
        await topicFilter.FillAsync(string.Empty, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        root = page.Locator($".topic-tree-row:has-text('{group}')").First;
        SetStage("verify Generic MQTT: wait for restored group row");
        await Assertions.Expect(root)
            .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("verify Generic MQTT: reselect restored group row");
        await root.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("verify Generic MQTT: verify topic row restored");
        await Assertions.Expect(message).Not.ToHaveCountAsync(0,
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    private async Task VerifySparkplugEmulatorAsync(
        IPage page, IMqttClient mqtt, string group, string node, string stateHost,
        CancellationToken token)
    {
        SetStage("verify Sparkplug: wait for Nodes tab");
        var nodesTab = page.GetByRole(AriaRole.Tab).Filter(new() { HasText = "Nodes" });
        await nodesTab.WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = MessageWaitTimeoutMs,
        }).WaitAsync(token);
        SetStage("verify Sparkplug: open Nodes tab");
        await nodesTab.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var nodeRow = page.Locator($".spb-node-row:has(.spb-node-id:text-is('{node}'))");
        SetStage("verify Sparkplug: wait for generated node row");
        await nodeRow.WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = MessageWaitTimeoutMs,
        }).WaitAsync(token);
        SetStage("verify Sparkplug: select generated node row");
        await nodeRow.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        SetStage("verify Sparkplug: verify selected node detail");
        await Assertions.Expect(page.Locator(".spb-detail-title").First)
            .ToHaveTextAsync(node, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var deviceRow = page.Locator(".spb-device-row").Filter(new() { HasText = "Device-1" });
        SetStage("verify Sparkplug: select Device-1");
        await deviceRow.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var deviceMetrics = page.Locator(".spb-device-metrics");
        SetStage("verify Sparkplug: wait for Metric-1");
        await Assertions.Expect(deviceMetrics.Locator(".spb-td-name")
                .Filter(new() { HasText = "Metric-1" }).First)
            .ToBeVisibleAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
        SetStage("verify Sparkplug: assert Metric-1 value is populated");
        await Assertions.Expect(page.Locator(
                ".spb-device-metrics .spb-metric-row:has(.spb-td-name:text-is('Metric-1')) .spb-td-value"))
            .Not.ToBeEmptyAsync(new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);

        var stateTopic = $"spBv1.0/STATE/{stateHost}";
        var statePayload = "ONLINE";
        SetStage("verify Sparkplug STATE: publish generated state message");
        await PublishAsync(mqtt, stateTopic, statePayload, token);
        SetStage("verify Sparkplug STATE: navigate to Browser tab");
        await NavigateToTab(page, "Browser", token);
        SetStage("verify Sparkplug STATE: select Sparkplug topic root");
        await page.Locator(".topic-tree-row:has-text('spBv1.0')").First.ClickAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        var stateRow = page.Locator(
            $".browser-split-wrap tr:has(td.pb-cell-topic:text-is('{stateTopic}'))"
            + $":has(td.pb-cell-payload:text-is('{statePayload}'))");
        SetStage("verify Sparkplug STATE: wait for generated state row");
        await stateRow.WaitForAsync(new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = MessageWaitTimeoutMs,
        }).WaitAsync(token);
    }
}
