using System.Globalization;
using Microsoft.Playwright;
using MQTTnet;
using MQTTnet.Protocol;

namespace MqttProbe.BrowserSmokeTests;

// Shared plumbing for the live MQTT flow browser smoke tests.
// Constants and helpers; test methods live in the sibling partial files.
public sealed partial class MqttLiveFlowsSmokeTests
{
    private const int ActionTimeoutMs = 15_000;
    private const int NavTimeoutMs = 40_000;
    private const int ScenarioBudgetSeconds = 180;
    private const int CleanupMqttSeconds = 10;
    private const int CleanupBrowserSeconds = 15;
    // Per best-effort UI phase, so worst-case emulator cleanup is twice this.
    private const int CleanupUiSeconds = 10;
    private const int SubscribeSettleTimeoutMs = 8_000;
    private const int SubscribeSettlePollMs = 250;
    private const int MessageWaitTimeoutMs = 15_000;

    private const string BrokerHostDefault = "localhost";
    private const int BrokerPortDefault = 1883;

    private const string ConnectionDialog = ".connection-dialog-content";
    private const string ConnectedChip = ".status-chip--connected";
    private const string DisconnectedChip = ".status-chip--disconnected";
    private const string PauseButton = ".app-actions-wide button:has-text('Pause')";
    private const string DisconnectButton = "button:has-text('Disconnect')";
    private const string ConnectButton = ".app-actions-wide button:has-text('Connect')";

    private string _stage = "startup";

    // ── Browser lifecycle ──────────────────────────────────────────────

    private async Task<(IPlaywright, IBrowser, IPage)> LaunchBrowserAsync(
        string appOrigin, CancellationToken token)
    {
        SetStage("browser launch");
        var playwright = await Playwright.CreateAsync().WaitAsync(token);
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }).WaitAsync(token);
        var context = await browser.NewContextAsync(new()
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new() { Width = 1280, Height = 900 },
        }).WaitAsync(token);
        var page = await context.NewPageAsync().WaitAsync(token);
        page.SetDefaultTimeout(ActionTimeoutMs);
        page.SetDefaultNavigationTimeout(NavTimeoutMs);
        return (playwright, browser, page);
    }

    private static async Task CleanupAsync(
        IMqttClient? mqtt, IBrowser? browser, IPlaywright? playwright)
    {
        if (mqtt is not null)
        {
            try
            {
                if (mqtt.IsConnected)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupMqttSeconds));
                    await mqtt.DisconnectAsync(new MqttClientDisconnectOptions(), cts.Token);
                }
            }
            catch { }
            mqtt.Dispose();
        }

        using var browserCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupBrowserSeconds));
        try
        {
            if (browser is not null)
                await browser.CloseAsync().WaitAsync(browserCts.Token);
        }
        catch { }

        playwright?.Dispose();
    }

    // The Web host outlives the browser context, so a scenario that fails between Start
    // and Stop leaves the emulator publishing and the circuit retained. Each phase gets its
    // own budget: a Stop that hangs must not leave Disconnect holding an already-cancelled
    // token, which would skip the click entirely. Cleanup runs after the scenario token is
    // spent, and neither phase may replace the original failure.
    private static async Task StopEmulatorBestEffortAsync(IPage? page)
    {
        if (page is null || page.IsClosed)
            return;

        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupUiSeconds));
            var stopToken = stopCts.Token;
            var emulationTab = page.GetByRole(AriaRole.Tab).Filter(new() { HasText = "Emulation" });
            if (await emulationTab.CountAsync().WaitAsync(stopToken) > 0)
            {
                await emulationTab.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(stopToken);
                var stopButton = page.Locator("button[title='Stop emulation']");
                if (await stopButton.IsVisibleAsync().WaitAsync(stopToken))
                {
                    await stopButton.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(stopToken);
                    await page.Locator("button[title='Start emulation']").WaitForAsync(new()
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = ActionTimeoutMs,
                    }).WaitAsync(stopToken);
                }
            }
        }
        catch { }

        try
        {
            using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupUiSeconds));
            var disconnectToken = disconnectCts.Token;
            var disconnect = page.Locator(DisconnectButton);
            if (await disconnect.IsVisibleAsync().WaitAsync(disconnectToken))
            {
                await disconnect.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(disconnectToken);
                await page.Locator(DisconnectedChip).WaitForAsync(new()
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = ActionTimeoutMs,
                }).WaitAsync(disconnectToken);
            }
        }
        catch { }
    }

    // ── Login & connection ─────────────────────────────────────────────

    private async Task LoginAsync(IPage page, string appOrigin, string username, string password,
        CancellationToken token)
    {
        SetStage("navigate to /Login");
        await page.GotoAsync($"{appOrigin}/Login", new()
        {
            Timeout = NavTimeoutMs,
            WaitUntil = WaitUntilState.DOMContentLoaded,
        }).WaitAsync(token);

        SetStage("fill credentials");
        await page.Locator("input#username").FillAsync(username, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);
        await page.Locator("input#password").FillAsync(password, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);
        await page.Locator("form button[type='submit']").ClickAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        SetStage("wait for app shell");
        await page.Locator(".mud-layout").First.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs }).WaitAsync(token);
        await page.Locator(ConnectionDialog).First.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs }).WaitAsync(token);
        AssertOrigin(page, appOrigin);
    }

    private async Task ConfigureAndConnectAsync(IPage page, string mqttHost, int mqttPort,
        string connName, string clientId, CancellationToken token)
    {
        SetStage("configure MQTT connection");
        var dialog = page.Locator(ConnectionDialog).First;
        await dialog.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);
        await dialog.Locator("[title='New connection']").ClickAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await dialog.GetByLabel("Name", new() { Exact = true })
            .FillAsync(connName, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await dialog.GetByLabel("Client ID", new() { Exact = true })
            .FillAsync(clientId, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await dialog.GetByLabel("Host", new() { Exact = true })
            .FillAsync(mqttHost, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await dialog.GetByLabel("Port", new() { Exact = true })
            .FillAsync(mqttPort.ToString(CultureInfo.InvariantCulture),
                new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var tlsCheckbox = dialog.GetByLabel("Use TLS", new() { Exact = true });
        if (await tlsCheckbox.IsCheckedAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token))
        {
            await tlsCheckbox.UncheckAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        }

        SetStage("click Connect");
        await dialog.Locator("[title='Connect']").ClickAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);

        var promptBtn = page.Locator(".mud-dialog-container button:has-text('Connect anyway')").First;
        var discardBtn = page.Locator("button:has-text('Discard')").First;
        var connectDeadline = DateTime.UtcNow.AddMilliseconds(NavTimeoutMs);
        while (DateTime.UtcNow < connectDeadline)
        {
            if (await page.Locator(ConnectedChip).First.IsVisibleAsync().WaitAsync(token)) break;
            if (await promptBtn.IsVisibleAsync().WaitAsync(token))
            {
                await promptBtn.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
                await promptBtn.WaitForAsync(
                    new() { State = WaitForSelectorState.Hidden, Timeout = ActionTimeoutMs }).WaitAsync(token);
                break;
            }
            if (await discardBtn.IsVisibleAsync().WaitAsync(token))
            {
                await discardBtn.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
                await discardBtn.WaitForAsync(
                    new() { State = WaitForSelectorState.Hidden, Timeout = ActionTimeoutMs }).WaitAsync(token);
                break;
            }
            await Task.Delay(250, token);
        }
    }

    private async Task WaitForConnectedAsync(IPage page, string connName, CancellationToken token,
        bool mobile = false)
    {
        SetStage("wait for connected chip");
        var chip = page.Locator(ConnectedChip).First;
        await chip.WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs }).WaitAsync(token);
        await Assertions.Expect(chip).ToHaveTextAsync(connName, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);

        SetStage("wait for connection dialog hidden");
        await page.Locator(ConnectionDialog).First.WaitForAsync(
            new() { State = WaitForSelectorState.Hidden, Timeout = ActionTimeoutMs }).WaitAsync(token);

        if (!mobile)
        {
            SetStage("wait for Pause button");
            await page.Locator(PauseButton).First.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs }).WaitAsync(token);
        }
    }

    // ── Tab navigation ─────────────────────────────────────────────────

    private static async Task NavigateToTab(IPage page, string tabName, CancellationToken token)
    {
        await page.GetByRole(AriaRole.Tab, new() { Name = tabName }).ClickAsync(
            new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // ── Selectors & locators ───────────────────────────────────────────

    private static ILocator PayloadRow(IPage page, string topic, string payload)
    {
        return page.Locator(
            $".browser-split-wrap tr"
            + $":has(td.pb-cell-topic:text-is('{topic}'))"
            + $":has(td.pb-cell-payload:text-is('{payload}'))");
    }

    // ── MQTT client helpers ────────────────────────────────────────────

    private static async Task<IMqttClient> ConnectMqttClientAsync(
        string host, int port, CancellationToken token)
    {
        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"live-smoke-{Guid.NewGuid():N}")
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

        var deadline = DateTime.UtcNow.AddMilliseconds(SubscribeSettleTimeoutMs);
        MqttClientPublishReasonCode code;
        while (true)
        {
            code = (await client.PublishAsync(msg, token)).ReasonCode;
            if (code != MqttClientPublishReasonCode.NoMatchingSubscribers || DateTime.UtcNow >= deadline)
                break;
            await Task.Delay(SubscribeSettlePollMs, token);
        }

        Assert.That(code, Is.EqualTo(MqttClientPublishReasonCode.Success),
            $"publish to {topic} failed: {code}");
        await Task.Delay(200, token);
    }

    // ── Environment & validation ───────────────────────────────────────

    private static void ValidateHostPort(out string host, out int port)
    {
        var rawHost = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_MQTT_HOST");
        if (rawHost is not null && string.IsNullOrWhiteSpace(rawHost))
            Assert.Fail("MQTTPROBE_TEST_MQTT_HOST is set but empty");
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

    private static string ResolveBaseUrl()
    {
        var baseUrl = (Environment.GetEnvironmentVariable("MQTTPROBE_TEST_BASE_URL")
                ?? "https://localhost:5001").TrimEnd('/');
        Assert.That(Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri), Is.True,
            "MQTTPROBE_TEST_BASE_URL must be an absolute URL");
        Assert.That(baseUri!.Scheme, Is.EqualTo("https"), "base URL must be https");
        Assert.That(baseUri.IsLoopback, Is.True, "base URL must be loopback");
        if (baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0
            || baseUri.Fragment.Length > 0 || baseUri.AbsolutePath != "/")
        {
            Assert.Fail("MQTTPROBE_TEST_BASE_URL must be a bare root origin");
        }
        return baseUri.GetLeftPart(UriPartial.Authority);
    }

    private static string ReadEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        Assert.That(value, Is.Not.Null.And.Not.Empty,
            $"{name} is not set; export it (see README.md)");
        return value!;
    }

    private static void AssertOrigin(IPage page, string expectedOrigin)
    {
        var url = page.MainFrame?.Url ?? string.Empty;
        var origin = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "(unparseable URL)";
        Assert.That(origin, Is.EqualTo(expectedOrigin),
            $"unexpected page origin (expected {expectedOrigin})");
    }

    private void SetStage(string stage) => _stage = stage;
}
