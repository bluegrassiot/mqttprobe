using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

[TestFixture]
public sealed class LocalAuthenticationSmokeTests
{
    private const int ActionTimeoutMs = 15_000;
    private const int NavTimeoutMs = 40_000;
    private const int ScenarioBudgetSeconds = 180;
    private const int CleanupTimeoutSeconds = 15;

    private const string UsernameInput = "input#username";
    private const string PasswordInput = "input#password";
    private const string LoginSubmit = "form button[type='submit']:not(.oidc-button)";
    private const string ErrorAlert = "div.error[role='alert']";
    private const string OidcButton = "button.oidc-button";
    private const string LogoutControl =
        "form.logout-action-form button[type='submit'], form.logout-menu-form button[type='submit']";
    private const string ConnectionDialog = ".connection-dialog-content";
    private const string DialogCancel = ".mud-dialog .connection-dialog-content button:has-text('Cancel')";

    private string _stage = "startup";

    [Test]
    [CancelAfter(ScenarioBudgetSeconds * 1000)]
    public async Task LocalLoginFlowInvalidAndValidLoginLogout(CancellationToken cancellationToken)
    {
        var appOrigin = ResolveBaseUrl();

        var username = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_USERNAME")?.Trim();
        var password = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_PASSWORD");
        Assert.That(username, Is.Not.Null.And.Not.Empty,
            "MQTTPROBE_TEST_USERNAME is not set; export it (see README.md)");
        Assert.That(password, Is.Not.Null.And.Not.Empty,
            "MQTTPROBE_TEST_PASSWORD is not set; export it (see README.md)");

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;

        using var scenarioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scenarioCts.CancelAfter(TimeSpan.FromSeconds(ScenarioBudgetSeconds));
        var scenarioToken = scenarioCts.Token;

        // On timeout, close context/browser to interrupt any active Playwright operation.
        using var registration = scenarioToken.Register(() =>
        {
            _ = context?.CloseAsync();
            _ = browser?.CloseAsync();
        });

        try
        {
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

            SetStage("origin guard");
            AssertOrigin(page, appOrigin, "login page");

            SetStage("assert local login form");
            await page.Locator(UsernameInput).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);
            await page.Locator(PasswordInput).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);

            SetStage("assert OIDC button absent");
            var oidcCount = await page.Locator(OidcButton).CountAsync().WaitAsync(scenarioToken);
            Assert.That(oidcCount, Is.EqualTo(0),
                "OIDC button must not be present on the local login page");

            SetStage("submit invalid password");
            var wrongPassword = $"Wrong-{Guid.NewGuid():N}";
            AssertOrigin(page, appOrigin, "before invalid credentials");
            await page.Locator(UsernameInput).First.FillAsync(username!, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator(PasswordInput).First.FillAsync(wrongPassword, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator(LoginSubmit).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);

            SetStage("assert visible error");
            await page.Locator(ErrorAlert).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);

            SetStage("assert no shell after invalid login");
            var shellAfterBad = await page.Locator(LogoutControl).CountAsync().WaitAsync(scenarioToken);
            Assert.That(shellAfterBad, Is.EqualTo(0),
                "app shell must not appear after invalid credentials");

            SetStage("sign in with correct credentials");
            AssertOrigin(page, appOrigin, "before correct credentials");
            await page.Locator(UsernameInput).First.FillAsync(username!, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator(PasswordInput).First.FillAsync(password!, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator(LoginSubmit).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);

            SetStage("wait for app shell");
            await page.Locator(LogoutControl).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = NavTimeoutMs,
            }).WaitAsync(scenarioToken);
            AssertOrigin(page, appOrigin, "after login");

            SetStage("dismiss connection dialog");
            var dialog = page.Locator(ConnectionDialog).First;
            await dialog.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);
            await page.Locator(DialogCancel).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await dialog.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);

            SetStage("click logout");
            await page.Locator(LogoutControl).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);

            SetStage("wait for /Login after logout");
            await page.Locator(UsernameInput).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = NavTimeoutMs,
            }).WaitAsync(scenarioToken);
            AssertLoginPath(page, "after logout");
            AssertOrigin(page, appOrigin, "after logout");

            SetStage("navigate to / (protected)");
            await page.GotoAsync($"{appOrigin}/", new PageGotoOptions
            {
                Timeout = NavTimeoutMs,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            }).WaitAsync(scenarioToken);

            SetStage("assert redirect to /Login");
            await page.Locator(UsernameInput).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = NavTimeoutMs,
            }).WaitAsync(scenarioToken);
            AssertLoginPath(page, "protected redirect");
            AssertOrigin(page, appOrigin, "protected redirect");

            SetStage("re-login");
            AssertOrigin(page, appOrigin, "before re-login credentials");
            await page.Locator(UsernameInput).First.FillAsync(username!, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator(PasswordInput).First.FillAsync(password!, new() { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await page.Locator(LoginSubmit).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);

            SetStage("wait for app shell after re-login");
            await page.Locator(LogoutControl).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = NavTimeoutMs,
            }).WaitAsync(scenarioToken);
            AssertOrigin(page, appOrigin, "after re-login");

            SetStage("dismiss connection dialog after re-login");
            var dialog2 = page.Locator(ConnectionDialog).First;
            await dialog2.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);
            await page.Locator(DialogCancel).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);
            await dialog2.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = ActionTimeoutMs,
            }).WaitAsync(scenarioToken);

            SetStage("final logout");
            await page.Locator(LogoutControl).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(scenarioToken);

            SetStage("assert /Login after final logout");
            await page.Locator(UsernameInput).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = NavTimeoutMs,
            }).WaitAsync(scenarioToken);
            AssertLoginPath(page, "after final logout");
            AssertOrigin(page, appOrigin, "after final logout");
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
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
            try
            {
                if (browser is not null)
                    await browser.CloseAsync().WaitAsync(cleanupCts.Token);
            }
            catch (Exception)
            {
            }

            playwright?.Dispose();
        }
    }

    private static string ResolveBaseUrl()
    {
        var baseUrl = (Environment.GetEnvironmentVariable("MQTTPROBE_TEST_BASE_URL")
                ?? "https://localhost:5081")
            .TrimEnd('/');
        Assert.That(Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri), Is.True,
            "MQTTPROBE_TEST_BASE_URL must be an absolute URL");
        Assert.That(baseUri!.Scheme, Is.EqualTo(Uri.UriSchemeHttps), "base URL must be https");
        Assert.That(baseUri.IsLoopback, Is.True, "base URL must be a loopback host");

        if (baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0 ||
            baseUri.AbsolutePath != "/")
        {
            Assert.Fail("MQTTPROBE_TEST_BASE_URL must be a bare root origin " +
                "(no user info, query string, fragment, or path)");
        }

        return baseUri.GetLeftPart(UriPartial.Authority);
    }

    // Fail closed: credentials typed only when the browser's parsed origin matches exactly.
    private static void AssertOrigin(IPage page, string expectedOrigin, string tag)
    {
        var url = page.MainFrame?.Url ?? string.Empty;
        var origin = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "(unparseable URL)";
        Assert.That(origin, Is.EqualTo(expectedOrigin),
            $"[{tag}] unexpected page origin (expected {expectedOrigin}); refusing to continue");
    }

    private static void AssertLoginPath(IPage page, string tag)
    {
        var url = page.MainFrame?.Url ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Assert.Fail($"[{tag}] unparseable URL; expected /Login");
            return;
        }

        Assert.That(uri.AbsolutePath, Is.EqualTo("/Login"),
            $"[{tag}] expected /Login path, got {uri.AbsolutePath}");
    }

    private void SetStage(string stage) => _stage = stage;
}
