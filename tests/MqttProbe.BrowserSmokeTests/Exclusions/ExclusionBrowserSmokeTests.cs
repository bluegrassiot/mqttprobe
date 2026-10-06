using System.Globalization;
using Microsoft.Playwright;
using MQTTnet;
using MQTTnet.Protocol;

namespace MqttProbe.BrowserSmokeTests;

[TestFixture]
public sealed class ExclusionBrowserSmokeTests
{
    private const int ActionTimeoutMs = 15_000;
    private const int NavTimeoutMs = 40_000;
    private const int ScenarioBudgetSeconds = 180;
    private const int CleanupMqttSeconds = 10;
    private const int CleanupBrowserSeconds = 15;
    private const int SubscribeSettleTimeoutMs = 8_000;
    private const int SubscribeSettlePollMs = 250;

    private const string ConnectionDialog = ".connection-dialog-content";
    private const string LogoutControl =
        "form.logout-action-form button[type='submit'], form.logout-menu-form button[type='submit']";

    private const string BrokerHostDefault = "localhost";
    private const int BrokerPortDefault = 1883;

    private const string TopicInput = ".exclude-editor-topic input";
    private const string AddExcludeButton = "button.exclude-editor-add[title='Add exclude topic']";
    private const string ExpansionPanelHeader =
        ".exclude-editor .mud-expand-panel .mud-expand-panel-header";

    private string _stage = "startup";

    private static IEnumerable<TestCaseData> ExclusionCases()
    {
        var exactRoot = $"exclude{Guid.NewGuid():N}";
        yield return new TestCaseData(
            exactRoot,
            new[] { $"{exactRoot}/blocked/a/temp" },
            new[] { $"{exactRoot}/blocked/a/temperature", $"{exactRoot}/keep/temp" },
            $"{exactRoot}/blocked/a/temp",
            $"{exactRoot}/#",
            new[] { $"{exactRoot}/blocked/a/temp" },
            new[] { $"{exactRoot}/blocked/a/temperature", $"{exactRoot}/keep/temp" })
            .SetName("ExactExclusion_{0}");

        var wildcardRoot = $"exclude{Guid.NewGuid():N}";
        yield return new TestCaseData(
            wildcardRoot,
            new[] { $"{wildcardRoot}/blocked/a/temp", $"{wildcardRoot}/blocked/b/nested/temp" },
            new[] { $"{wildcardRoot}/blocked", $"{wildcardRoot}/keep/temp" },
            $"{wildcardRoot}/blocked/+/#",
            $"{wildcardRoot}/#",
            new[] { $"{wildcardRoot}/blocked/a/temp", $"{wildcardRoot}/blocked/b/nested/temp" },
            new[] { $"{wildcardRoot}/blocked", $"{wildcardRoot}/keep/temp" })
            .SetName("WildcardExclusion_{0}");
    }

    [TestCaseSource(nameof(ExclusionCases))]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public Task ExclusionFilterBlocksAndResumes(
        string root,
        string[] baselineTopics,
        string[] controlTopics,
        string excludeFilter,
        string subscribeFilter,
        string[] expectedBlocked,
        string[] expectedUnaffected,
        CancellationToken cancellationToken)
        => RunExclusionAsync(root, baselineTopics, controlTopics, excludeFilter,
            subscribeFilter, expectedBlocked, expectedUnaffected, cancellationToken);

    private async Task RunExclusionAsync(
        string root,
        string[] baselineTopics,
        string[] controlTopics,
        string excludeFilter,
        string subscribeFilter,
        string[] expectedBlocked,
        string[] expectedUnaffected,
        CancellationToken cancellationToken)
    {
        ValidateHostPort(out var mqttHost, out var mqttPort);
        var baseUrl = (Environment.GetEnvironmentVariable("MQTTPROBE_TEST_BASE_URL")
                ?? "https://localhost:5081").TrimEnd('/');
        Assert.That(Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri), Is.True,
            "MQTTPROBE_TEST_BASE_URL must be absolute");
        Assert.That(baseUri!.Scheme, Is.EqualTo("https"), "base URL must be https");
        Assert.That(baseUri.IsLoopback, Is.True, "base URL must be loopback");
        if (baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0
            || baseUri.Fragment.Length > 0 || baseUri.AbsolutePath != "/")
        {
            Assert.Fail("MQTTPROBE_TEST_BASE_URL must be a bare root origin");
        }

        var username = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_USERNAME")?.Trim();
        var password = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_PASSWORD");
        Assert.That(username, Is.Not.Null.And.Not.Empty,
            "MQTTPROBE_TEST_USERNAME is not set; export it (see README.md)");
        Assert.That(password, Is.Not.Null.And.Not.Empty,
            "MQTTPROBE_TEST_PASSWORD is not set; export it (see README.md)");

        var appOrigin = baseUri.GetLeftPart(UriPartial.Authority);
        var connName = $"smoke-{root}";
        var clientId = $"smoke-{Guid.NewGuid():N}"[..22];
        var publishCount = 0;

        IMqttClient? mqtt = null;
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;

        using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scenarioCts.CancelAfter(TimeSpan.FromSeconds(ScenarioBudgetSeconds));
        var scenarioToken = scenarioCts.Token;

        using var registration = scenarioToken.Register(() =>
        {
            SuppressFaults(context?.CloseAsync());
            SuppressFaults(browser?.CloseAsync());
        });

        try
        {
            mqtt = await ConnectMqttClientAsync(mqttHost, mqttPort, scenarioToken);

            SetStage("browser launch");
            playwright = await Playwright.CreateAsync().WaitAsync(scenarioToken);
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
            }).WaitAsync(scenarioToken);
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            }).WaitAsync(scenarioToken);
            var page = await context.NewPageAsync().WaitAsync(scenarioToken);
            page.SetDefaultTimeout(ActionTimeoutMs);
            page.SetDefaultNavigationTimeout(NavTimeoutMs);

            SetStage("navigate to /Login");
            await page.GotoAsync($"{appOrigin}/Login", new PageGotoOptions
            {
                Timeout = NavTimeoutMs,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            }).WaitAsync(scenarioToken);

            SetStage("sign-in: check origin");
            AssertOrigin(page, appOrigin);
            SetStage("sign-in: fill credentials");
            await page.Locator("input#username").FillAsync(username!, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator("input#password").FillAsync(password!, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator("form button[type='submit']").ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);

            SetStage("sign-in: wait for app shell");
            await page.Locator(LogoutControl).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = NavTimeoutMs,
            }).WaitAsync(scenarioToken);
            AssertOrigin(page, appOrigin);

            SetStage("configure MQTT connection");
            var dialog = page.Locator(ConnectionDialog).First;
            await dialog.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);
            await dialog.Locator("[title='New connection']").ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await dialog.GetByLabel("Name", new() { Exact = true })
                .FillAsync(connName, new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await dialog.GetByLabel("Client ID", new() { Exact = true })
                .FillAsync(clientId, new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await dialog.GetByLabel("Host", new() { Exact = true })
                .FillAsync(mqttHost, new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await dialog.GetByLabel("Port", new() { Exact = true })
                .FillAsync(mqttPort.ToString(CultureInfo.InvariantCulture),
                    new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);

            var tlsCheckbox = dialog.GetByLabel("Use TLS", new() { Exact = true });
            if (await tlsCheckbox.IsCheckedAsync(new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken))
            {
                await tlsCheckbox.UncheckAsync(new() { Timeout = ActionTimeoutMs })
                    .WaitAsync(scenarioToken);
            }

            SetStage("click Connect");
            await dialog.Locator("[title='Connect']").ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);

            // Bounded poll: "Connect anyway" prompt or connected chip
            var promptBtn = page.Locator(".mud-dialog-container button:has-text('Connect anyway')").First;
            var connectedChip = page.Locator(".status-chip--connected").First;
            var connectDeadline = DateTime.UtcNow.AddMilliseconds(NavTimeoutMs);
            while (DateTime.UtcNow < connectDeadline)
            {
                if (await connectedChip.IsVisibleAsync().WaitAsync(scenarioToken)) break;
                if (await promptBtn.IsVisibleAsync().WaitAsync(scenarioToken))
                {
                    SetStage("handle unsaved changes dialog");
                    await promptBtn.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                        .WaitAsync(scenarioToken);
                    await promptBtn.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Hidden,
                        Timeout = ActionTimeoutMs,
                    }).WaitAsync(scenarioToken);
                    break;
                }
                await Task.Delay(250, scenarioToken);
            }

            SetStage("wait for MQTT connection");
            await connectedChip.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = NavTimeoutMs,
            }).WaitAsync(scenarioToken);
            await Assertions.Expect(connectedChip)
                .ToHaveTextAsync(connName, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);

            SetStage("wait for connection dialog hidden");
            await dialog.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);

            await page.Locator(".app-actions-wide button:has-text('Pause')").First.WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = ActionTimeoutMs,
                }).WaitAsync(scenarioToken);

            SetStage("add subscription");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Subscriptions" }).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            var subscriptionsList = page.Locator("role=region[name='Subscriptions list']");
            await subscriptionsList
                .Locator(".subscription-editor-topic input").First
                .FillAsync(subscribeFilter, new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator("button[title='Add subscription']").ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);

            // SubscriptionManager records the topic only after SubscribeAsync returns, so the
            // listed row is the app's proof the SUBSCRIBE landed. Fail here when it never does
            // instead of burning the publish-retry window downstream.
            await subscriptionsList
                .Locator($".subscription-editor-table-wrap .mud-table-row"
                    + $":has(td:text-is('{subscribeFilter}'))")
                .First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = SubscribeSettleTimeoutMs,
                }).WaitAsync(scenarioToken);

            SetStage("publish baseline");
            for (var i = 0; i < baselineTopics.Length; i++)
            {
                await PublishAsync(mqtt, baselineTopics[i], $"baseline-{root}-{i + 1}", scenarioToken);
            }
            for (var i = 0; i < controlTopics.Length; i++)
            {
                await PublishAsync(mqtt, controlTopics[i], $"baseline-ctrl-{root}-{i + 1}", scenarioToken);
            }

            SetStage("verify baseline in browser");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Browser" }).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator($".topic-tree-row:has-text('{root}')").First.ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            for (var i = 0; i < baselineTopics.Length; i++)
            {
                var payload = $"baseline-{root}-{i + 1}";
                await Assertions.Expect(PayloadRow(page, baselineTopics[i], payload))
                    .ToBeVisibleAsync(new() { Timeout = NavTimeoutMs }).WaitAsync(scenarioToken);
            }
            for (var i = 0; i < controlTopics.Length; i++)
            {
                var payload = $"baseline-ctrl-{root}-{i + 1}";
                await Assertions.Expect(PayloadRow(page, controlTopics[i], payload))
                    .ToBeVisibleAsync(new() { Timeout = NavTimeoutMs }).WaitAsync(scenarioToken);
            }

            SetStage("add exclude filter");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Subscriptions" }).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator(ExpansionPanelHeader).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator(TopicInput).WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);
            await page.Locator(TopicInput).FillAsync(excludeFilter,
                new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator(AddExcludeButton).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await Assertions.Expect(page.Locator(".exclude-editor .count-chip"))
                .ToHaveTextAsync("1", new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await Assertions.Expect(page.Locator(
                ".exclude-editor-table-wrap .mud-table-body .mud-table-row"))
                .ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await Assertions.Expect(page.Locator(
                $".exclude-editor-table-wrap .mud-table-body .mud-table-row"
                + $":has-text('{excludeFilter}')"))
                .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);

            SetStage("verify absent after exclude");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Browser" }).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator($".topic-tree-row:has-text('{root}')").First.ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            foreach (var topic in expectedBlocked)
            {
                await Assertions.Expect(PayloadRows(page, topic))
                    .ToHaveCountAsync(0, new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            }
            for (var i = 0; i < controlTopics.Length; i++)
            {
                var payload = $"baseline-ctrl-{root}-{i + 1}";
                await Assertions.Expect(PayloadRow(page, controlTopics[i], payload))
                    .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            }

            SetStage("publish blocked + sentinel");
            foreach (var topic in expectedBlocked)
            {
                await PublishAsync(mqtt, topic, $"blocked-{root}", scenarioToken);
            }
            var sentinelTopic = expectedUnaffected[0];
            var sentinel = $"sentinel-{root}-{++publishCount}";
            await PublishAsync(mqtt, sentinelTopic, sentinel, scenarioToken);

            await Assertions.Expect(PayloadRow(page, sentinelTopic, sentinel))
                .ToBeVisibleAsync(new() { Timeout = NavTimeoutMs }).WaitAsync(scenarioToken);

            SetStage("verify blocked absent (~3s)");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var topic in expectedBlocked)
                {
                    var count = await PayloadRow(page, topic, $"blocked-{root}").CountAsync()
                        .WaitAsync(scenarioToken);
                    Assert.That(count, Is.EqualTo(0),
                        $"blocked topic {topic} with blocked payload observed during observation window");
                }
                await Task.Delay(500, scenarioToken);
            }
            for (var i = 0; i < controlTopics.Length; i++)
            {
                var payload = $"baseline-ctrl-{root}-{i + 1}";
                await Assertions.Expect(PayloadRow(page, controlTopics[i], payload))
                    .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            }

            SetStage("remove exclude filter");
            await page.GetByRole(AriaRole.Tab, new() { Name = "Subscriptions" }).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator(
                ".exclude-editor-table-wrap .mud-table-body label.mud-checkbox").First.ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator(
                ".exclude-editor-actions button[title='Remove']").ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await Assertions.Expect(page.Locator(".exclude-editor .count-chip"))
                .ToHaveTextAsync("0", new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            var filterRowCount = await page.Locator(
                $".exclude-editor-table-wrap .mud-table-body .mud-table-row"
                + $":has-text('{excludeFilter}')")
                .CountAsync().WaitAsync(scenarioToken);
            Assert.That(filterRowCount, Is.EqualTo(0),
                $"exclude filter row for '{excludeFilter}' should be absent after removal");

            SetStage("verify resume after removal");
            var resumePayload = $"resume-{root}-{++publishCount}";
            await PublishAsync(mqtt, expectedBlocked[0], resumePayload, scenarioToken);
            await page.GetByRole(AriaRole.Tab, new() { Name = "Browser" }).ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator($".topic-tree-row:has-text('{root}')").First.ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await Assertions.Expect(PayloadRow(page, expectedBlocked[0], resumePayload))
                .ToBeVisibleAsync(new() { Timeout = NavTimeoutMs }).WaitAsync(scenarioToken);
            for (var i = 0; i < baselineTopics.Length; i++)
            {
                var baselinePayload = $"baseline-{root}-{i + 1}";
                var historicalCount = await PayloadRow(page, baselineTopics[i], baselinePayload)
                    .CountAsync().WaitAsync(scenarioToken);
                Assert.That(historicalCount, Is.EqualTo(0),
                    $"historical baseline on {baselineTopics[i]} should be absent after resume");
            }
            var blockedMarker = $"blocked-{root}";
            foreach (var topic in expectedBlocked)
            {
                var blockedCount = await PayloadRow(page, topic, blockedMarker).CountAsync()
                    .WaitAsync(scenarioToken);
                Assert.That(blockedCount, Is.EqualTo(0),
                    $"blocked marker on {topic} should be absent after resume");
            }
            for (var i = 0; i < controlTopics.Length; i++)
            {
                var payload = $"baseline-ctrl-{root}-{i + 1}";
                await Assertions.Expect(PayloadRow(page, controlTopics[i], payload))
                    .ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            }

            SetStage("disconnect");
            await page.Locator($"button:has-text('Disconnect')").ClickAsync(
                new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(scenarioToken);
            await page.Locator(".status-chip--disconnected").First.WaitForAsync(
                new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = ActionTimeoutMs,
                }).WaitAsync(scenarioToken);
        }
        catch (OperationCanceledException) when (scenarioToken.IsCancellationRequested)
        {
            Assert.Fail($"[{_stage}] exceeded {ScenarioBudgetSeconds}s scenario budget");
        }
        catch (PlaywrightException)
        {
            Assert.Fail($"[{_stage}] Playwright error");
        }
        catch (AssertionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Assert.Fail($"[{_stage}] unexpected {ex.GetType().Name}");
        }
        finally
        {
            if (mqtt is not null)
            {
                try
                {
                    if (mqtt.IsConnected)
                    {
                        using var mqttCts = new CancellationTokenSource(
                            TimeSpan.FromSeconds(CleanupMqttSeconds));
                        await mqtt.DisconnectAsync(
                            new MqttClientDisconnectOptions(), mqttCts.Token);
                    }
                }
                catch { }
                mqtt.Dispose();
            }

            using var browserCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(CleanupBrowserSeconds));
            try
            {
                if (browser is not null)
                    await browser.CloseAsync().WaitAsync(browserCts.Token);
            }
            catch { }

            playwright?.Dispose();
        }
    }

    private static void SuppressFaults(Task? task)
    {
        if (task is not null)
            _ = task.ContinueWith(static t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private static ILocator PayloadRow(IPage page, string topic, string payload)
    {
        return page.Locator(
            $".browser-split-wrap tr"
            + $":has(td.pb-cell-topic:text-is('{topic}'))"
            + $":has(td.pb-cell-payload:text-is('{payload}'))");
    }

    private static ILocator PayloadRows(IPage page, string topic)
    {
        return page.Locator(
            $".browser-split-wrap tr:has(td.pb-cell-topic:text-is('{topic}'))");
    }

    private static void ValidateHostPort(out string host, out int port)
    {
        var rawHost = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_MQTT_HOST");
        if (rawHost is not null && string.IsNullOrWhiteSpace(rawHost))
            Assert.Fail("MQTTPROBE_TEST_MQTT_HOST is set but empty; unset for the default or provide a host");
        host = rawHost is null ? BrokerHostDefault : rawHost.Trim();

        var rawPort = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_MQTT_PORT");
        if (rawPort is null)
        {
            port = BrokerPortDefault;
        }
        else if (!int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                 || port is < 1 or > 65535)
        {
            Assert.Fail("MQTTPROBE_TEST_MQTT_PORT must be an integer 1-65535");
        }
    }

    private static async Task<IMqttClient> ConnectMqttClientAsync(
        string host, int port, CancellationToken token)
    {
        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"excl-smoke-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();
        try
        {
            await client.ConnectAsync(options, token);
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    private static async Task PublishAsync(
        IMqttClient client, string topic, string payload, CancellationToken token)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        // Mosquitto answers NoMatchingSubscribers until the app's SUBSCRIBE reaches the broker,
        // so a publish issued right after "Add subscription" races that round trip. That reason
        // code also means nothing was delivered anywhere, so retrying cannot duplicate a message.
        // Any other code fails at once.
        var deadline = DateTime.UtcNow.AddMilliseconds(SubscribeSettleTimeoutMs);
        MqttClientPublishReasonCode code;
        while (true)
        {
            code = (await client.PublishAsync(msg, token)).ReasonCode;
            if (code != MqttClientPublishReasonCode.NoMatchingSubscribers || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(SubscribeSettlePollMs, token);
        }

        Assert.That(code, Is.EqualTo(MqttClientPublishReasonCode.Success),
            $"publish to {topic} failed: {code}");
        await Task.Delay(200, token);
    }

    private static void AssertOrigin(IPage page, string expectedOrigin)
    {
        var url = page.MainFrame?.Url ?? string.Empty;
        var origin = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "(unparseable URL)";
        Assert.That(origin, Is.EqualTo(expectedOrigin),
            $"unexpected page origin (expected {expectedOrigin}); refusing to continue");
    }

    private void SetStage(string stage) => _stage = stage;
}
