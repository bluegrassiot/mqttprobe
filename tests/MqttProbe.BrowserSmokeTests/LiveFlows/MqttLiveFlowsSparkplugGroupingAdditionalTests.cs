using System.Globalization;
using Microsoft.Playwright;
using MQTTnet;

namespace MqttProbe.BrowserSmokeTests;

public sealed partial class MqttLiveFlowsSmokeTests
{

    [Test]
    [Category("SparkplugGrouping")]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task CollapsedSelectedGroupUpdatesAndReclickKeepsDetail(
        CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var id = Guid.NewGuid().ToString("N");
        var group = $"g{id[..8]}";
        var node = $"n{id[..6]}";
        var device1 = $"d{id.Substring(6, 4)}";
        var device2 = $"d{id.Substring(10, 4)}";
        var device3 = $"d{id.Substring(14, 4)}";
        var connName = $"smoke-{group}";
        var clientId = $"smoke-{id}"[..22];

        var dbirth1 = DeviceTopic(group, "DBIRTH", node, device1);
        var ddata1 = DeviceTopic(group, "DDATA", node, device1);
        var ddata2 = DeviceTopic(group, "DDATA", node, device2);
        var ddata3 = DeviceTopic(group, "DDATA", node, device3);

        var otherGroup = $"o{id[..8]}";
        var otherNode = $"x{id[..6]}";
        var otherData = NodeTopic(otherGroup, "NDATA", otherNode);

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

            await LoginForGroupingAsync(page, appOrigin, username, password, token);
            await ConfigureAndConnectAsync(page, mqttHost, mqttPort, connName, clientId, token);
            await WaitForConnectedAsync(page, connName, token);

            await SubscribeAsync(page, $"{SparkplugNamespace}/#", token);
            await EnsureSparkplugGroupingAsync(page, token);
            await ClearAllStoredMessagesAsync(page, token);

            SetStage("one device present, node selected with an open detail");
            await PublishAsync(mqtt, dbirth1, "device-birth", token);
            await PublishAsync(mqtt, ddata1, "device-1", token);
            await AwaitNamespaceMessageCountAsync(page, 2, token);
            await ExpandAllTreeAsync(page, token);
            await WaitForGroupRowAsync(page, node, token);
            await ClickRowBodyAsync(DesktopVirtualRow(page, node), token);
            await OpenDesktopDetailAsync(page, ddata1, token);

            SetStage("re-clicking the same node keeps the selected message detail");
            await ClickRowBodyAsync(DesktopVirtualRow(page, node), token);
            Assert.That(await DetailTopicTextAsync(page, token), Is.EqualTo(ddata1),
                "re-selecting the same grouping row must not move the selection or drop the detail");
            await CloseDesktopDetailAsync(page, token);

            SetStage("collapsed selected node still gains a new device member");
            await ClickGroupChevronAsync(page, node, token);
            await PublishAsync(mqtt, ddata2, "device-2", token);
            await ExpectDesktopPayloadTopicAsync(page, ddata2, token);

            SetStage("unrelated traffic does not join the selection");
            var membership = await DesktopPayloadRowCountAsync(page, token);
            await PublishAsync(mqtt, otherData, "unrelated", token);

            await WaitForTreeRowLabelAsync(page, otherGroup, token);

            await PublishAsync(mqtt, ddata3, "device-3", token);
            await ExpectDesktopPayloadTopicAsync(page, ddata3, token);
            await ExpectNoDesktopPayloadTopicAsync(page, otherData, token);
            Assert.That(await DesktopPayloadRowCountAsync(page, token), Is.EqualTo(membership + 1),
                "only the in-scope sentinel may join the selected row's membership");

            SetStage("disconnect");
            await page.Locator(DisconnectButton).ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await WaitForDisconnectedAsync(page, token);
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

    [Test]
    [Category("SparkplugGrouping")]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task MobilePickerSelectionStaysLiveAndLabelIsReadable(
        CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var id = Guid.NewGuid().ToString("N");
        var group = $"g{id[..8]}";
        var node = $"n{id[..6]}";
        var connName = $"smoke-{group}";
        var clientId = $"smoke-{id}"[..22];

        var nbirth = NodeTopic(group, "NBIRTH", node);
        var ndata = NodeTopic(group, "NDATA", node);
        var ndeath = NodeTopic(group, "NDEATH", node);

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

            await LoginForGroupingAsync(page, appOrigin, username, password, token);
            await ConfigureAndConnectAsync(page, mqttHost, mqttPort, connName, clientId, token);
            await WaitForConnectedAsync(page, connName, token);

            await SubscribeAsync(page, $"{SparkplugNamespace}/{group}/#", token);
            await EnsureSparkplugGroupingAsync(page, token);
            await ClearAllStoredMessagesAsync(page, token);

            SetStage("two node message types present");
            await PublishAsync(mqtt, nbirth, "birth", token);
            await PublishAsync(mqtt, ndata, "data", token);

            await AwaitSeedReceiptAsync(page, token, (nbirth, "birth"), (ndata, "data"));

            SetStage("switch to the mobile viewport");
            await ResizeToMobileAsync(page, token);

            SetStage("select the grouped node through the mobile picker");
            await OpenMobilePickerAndSelectAsync(page, node, token);

            SetStage("the mobile label is readable and leaks no internal key");
            var label = await MobileSelectionLabelAsync(page, node, token);
            Assert.That(label, Is.Not.Empty, "the mobile topic bar should show a selection label");
            Assert.That(label.Contains(InternalKeyMarker, StringComparison.Ordinal), Is.False,
                "the mobile label must not expose the internal grouping key");
            Assert.That(label.Contains(NullCharacter, StringComparison.Ordinal), Is.False,
                "the mobile label must not contain a NUL byte from the internal key");
            Assert.That(label.Contains(node, StringComparison.Ordinal), Is.True,
                "the mobile label should name the selected node");

            SetStage("a new message type updates the closed-picker selection");
            var countBefore = await MobileCountChipAsync(page, token);
            await PublishAsync(mqtt, ndeath, "death", token);
            await ExpectMobilePayloadTopicAsync(page, ndeath, token);

            if (countBefore is int before)
                await Assertions.Expect(await UniqueAsync(page.Locator(MobileCountChipSelector), token))
                    .ToHaveTextAsync((before + 1).ToString(CultureInfo.InvariantCulture),
                        new() { Timeout = ActionTimeoutMs })
                    .WaitAsync(token);

            SetStage("label still readable after fresh traffic");
            var labelAfter = await MobileSelectionLabelAsync(page, node, token);
            Assert.That(labelAfter.Contains(InternalKeyMarker, StringComparison.Ordinal), Is.False);
            Assert.That(labelAfter.Contains(NullCharacter, StringComparison.Ordinal), Is.False);

            SetStage("disconnect at mobile");
            await DisconnectAtMobileAsync(page, token);
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

    [Test]
    [Category("SparkplugGrouping")]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task PurgingOneSelectedMemberClearsSelectionAndFreshTrafficDoesNotResurrect(
        CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var appOrigin = ResolveBaseUrl();
        var username = ReadEnv("MQTTPROBE_TEST_USERNAME");
        var password = ReadEnv("MQTTPROBE_TEST_PASSWORD");

        var id = Guid.NewGuid().ToString("N");
        var group = $"g{id[..8]}";
        var node = $"n{id[..6]}";
        var device = $"d{id.Substring(6, 4)}";
        var connName = $"smoke-{group}";
        var clientId = $"smoke-{id}"[..22];

        var nbirth = NodeTopic(group, "NBIRTH", node);
        var ndata = NodeTopic(group, "NDATA", node);
        var ddata = DeviceTopic(group, "DDATA", node, device);

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

            await LoginForGroupingAsync(page, appOrigin, username, password, token);
            await ConfigureAndConnectAsync(page, mqttHost, mqttPort, connName, clientId, token);
            await WaitForConnectedAsync(page, connName, token);

            await SubscribeAsync(page, $"{SparkplugNamespace}/{group}/#", token);
            await EnsureSparkplugGroupingAsync(page, token);
            await ClearAllStoredMessagesAsync(page, token);

            SetStage("select a node whose scope spans node and device topics");
            await PublishAsync(mqtt, nbirth, "birth", token);
            await PublishAsync(mqtt, ndata, "data", token);
            await PublishAsync(mqtt, ddata, "device-data", token);
            await AwaitSeedReceiptAsync(page, token,
                (nbirth, "birth"), (ndata, "data"), (ddata, "device-data"));
            await ExpandAllTreeAsync(page, token);
            await WaitForGroupRowAsync(page, node, token);
            await ClickRowBodyAsync(DesktopVirtualRow(page, node), token);
            await ExpectDesktopPayloadTopicAsync(page, nbirth, token);
            await ExpectDesktopPayloadTopicAsync(page, ndata, token);

            SetStage("purge one selected member through an exclude filter");
            var statBeforePurge = await NodeStatTextAsync(page, node, token);
            await AddExcludeFilterAsync(page, ndata, token);
            await NavigateToTab(page, "Browser", token);

            SetStage("purging one member clears the whole selection and list");
            await ExpectNodeStatChangedAsync(page, node, statBeforePurge, token);
            await Assertions.Expect(page.Locator(SelectedRowSelector))
                .ToHaveCountAsync(0, new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            await Assertions.Expect(page.Locator(DesktopPayloadRowSelector))
                .ToHaveCountAsync(0, new() { Timeout = MessageWaitTimeoutMs }).WaitAsync(token);
            await ObserveNoSelectionAndEmptyListStableAsync(
                page, TimeSpan.FromMilliseconds(StabilityWindowMs), token);

            SetStage("fresh traffic on the purged path does not resurrect the selection");
            await RemoveExcludeFilterAsync(page, token);
            await NavigateToTab(page, "Browser", token);
            await PublishAsync(mqtt, ndata, "fresh-data", token);
            await AwaitNamespaceMessageCountAsync(page, 3, token);
            await ExpandAllTreeAsync(page, token);
            await WaitForTreeRowLabelAsync(page, ndata, token);
            await ObserveNoSelectionAndEmptyListStableAsync(
                page, TimeSpan.FromMilliseconds(StabilityWindowMs), token);

            SetStage("disconnect");
            await page.Locator(DisconnectButton).ClickAsync(
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await WaitForDisconnectedAsync(page, token);
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
