using Microsoft.Playwright;
using MQTTnet;

namespace MqttProbe.BrowserSmokeTests;

public sealed partial class MqttLiveFlowsSmokeTests
{

    [Test]
    [Category("SparkplugGrouping")]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task GroupedTreeRowBodySelectsDevicesExcludeNodeTraffic(
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
        var dbirth = DeviceTopic(group, "DBIRTH", node, device);
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

            SetStage("publish grouped Sparkplug traffic");
            await PublishAsync(mqtt, nbirth, "birth", token);
            await PublishAsync(mqtt, ndata, "data", token);
            await PublishAsync(mqtt, dbirth, "device-birth", token);
            await PublishAsync(mqtt, ddata, "device-data", token);

            await AwaitSeedReceiptAsync(page, token,
                (nbirth, "birth"), (ndata, "data"), (dbirth, "device-birth"), (ddata, "device-data"));

            SetStage("expand the grouped tree");
            await ExpandAllTreeAsync(page, token);
            await WaitForTreeRowLabelAsync(page, group, token);
            await WaitForGroupRowAsync(page, node, token);
            await WaitForGroupRowAsync(page, device, token);
            Assert.That(await DesktopVirtualRow(page, "Devices").CountAsync().WaitAsync(token),
                Is.Zero, "the flat Sparkplug hierarchy must not contain a Devices wrapper row");
            var nodeIndent = await DesktopVirtualRow(page, node).Locator(".topic-tree-indent")
                .GetAttributeAsync("width").WaitAsync(token);
            var deviceIndent = await DesktopVirtualRow(page, device).Locator(".topic-tree-indent")
                .GetAttributeAsync("width").WaitAsync(token);
            Assert.That(int.Parse(deviceIndent!, System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(int.Parse(nodeIndent!, System.Globalization.CultureInfo.InvariantCulture) + 16),
                "the device row must be directly beneath its node");

            SetStage("virtual grouping rows are marked");
            Assert.That(await page.Locator(GroupingRowSelector).CountAsync().WaitAsync(token),
                Is.GreaterThan(0), "grouping rows should carry the virtual-row marker");

            SetStage("row body selects without toggling expansion");
            var rowsBefore = await TreeRowCountAsync(page, token);
            await ClickRowBodyAsync(DesktopVirtualRow(page, node), token);
            await Assertions.Expect(page.Locator(SelectedRowSelector))
                .ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            Assert.That(await TreeRowCountAsync(page, token), Is.EqualTo(rowsBefore),
                "clicking the row body must select, not expand or collapse");

            SetStage("chevron collapses the selected row without moving the selection");
            await ClickGroupChevronAsync(page, node, token);
            await Assertions.Expect(DesktopVirtualRow(page, "NDATA"))
                .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await Assertions.Expect(page.Locator(
                    $"{SelectedRowSelector}:has({GroupLabelSelector}:text-is('{node}'))"))
                .ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await ClickGroupChevronAsync(page, node, token);

            SetStage("device selects its topics only");
            await ClickRowBodyAsync(DesktopVirtualRow(page, device), token);
            await ExpectDesktopPayloadTopicAsync(page, dbirth, token);
            await ExpectDesktopPayloadTopicAsync(page, ddata, token);
            await ExpectNoDesktopPayloadTopicAsync(page, nbirth, token);
            await ExpectNoDesktopPayloadTopicAsync(page, ndata, token);

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
    public async Task SelectedNodeGainsFirstNewMessageTypeAndDeviceVerb(
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
        var dbirth = DeviceTopic(group, "DBIRTH", node, device);
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

            SetStage("node is born only, then selected");
            await PublishAsync(mqtt, nbirth, "birth", token);
            await AwaitSeedReceiptAsync(page, token, (nbirth, "birth"));
            await ExpandAllTreeAsync(page, token);
            await WaitForGroupRowAsync(page, node, token);
            await ClickRowBodyAsync(DesktopVirtualRow(page, node), token);
            await ExpectDesktopPayloadTopicAsync(page, nbirth, token);

            SetStage("first NDATA joins the selected node");
            var beforeData = await DesktopPayloadRowCountAsync(page, token);
            await PublishAsync(mqtt, ndata, "data", token);
            await ExpectDesktopPayloadTopicAsync(page, ndata, token);
            Assert.That(await DesktopPayloadRowCountAsync(page, token), Is.EqualTo(beforeData + 1),
                "a first new node message type must join the selected node's list");

            SetStage("a device appearing under the selected node joins its list");
            await PublishAsync(mqtt, ddata, "device-data", token);
            await ExpectDesktopPayloadTopicAsync(page, ddata, token);

            SetStage("selected device gains its first new verb");
            await WaitForGroupRowAsync(page, device, token);
            await ClickRowBodyAsync(DesktopVirtualRow(page, device), token);
            await ExpectDesktopPayloadTopicAsync(page, ddata, token);
            await ExpectNoDesktopPayloadTopicAsync(page, nbirth, token);
            await PublishAsync(mqtt, dbirth, "device-birth", token);
            await ExpectDesktopPayloadTopicAsync(page, dbirth, token);

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
    public async Task SelectedDeviceDoesNotGainOtherDeviceMessages(CancellationToken cancellationToken)
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
        var connName = $"smoke-{group}";
        var clientId = $"smoke-{id}"[..22];

        var ddata1 = DeviceTopic(group, "DDATA", node, device1);
        var ddata2 = DeviceTopic(group, "DDATA", node, device2);

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

            SetStage("one device present, actual device row selected");
            await PublishAsync(mqtt, ddata1, "device-1", token);
            await AwaitSeedReceiptAsync(page, token, (ddata1, "device-1"));
            await ExpandAllTreeAsync(page, token);
            await WaitForGroupRowAsync(page, device1, token);
            await ClickRowBodyAsync(DesktopVirtualRow(page, device1), token);
            await ExpectDesktopPayloadTopicAsync(page, ddata1, token);
            var beforeDevice2 = await DesktopPayloadRowCountAsync(page, token);

            SetStage("message from a different device remains outside the selection");
            await PublishAsync(mqtt, ddata2, "device-2", token);
            await ExpectNoDesktopPayloadTopicAsync(page, ddata2, token);
            Assert.That(await DesktopPayloadRowCountAsync(page, token), Is.EqualTo(beforeDevice2),
                "a different device's message must not join the selected device's list");

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
