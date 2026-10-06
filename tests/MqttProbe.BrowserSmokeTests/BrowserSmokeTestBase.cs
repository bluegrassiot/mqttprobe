using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

// Shared plumbing for the opt-in provider smoke tests: env-only credentials, a headed
// browser with one fresh context per test, bounded waits, and one end-session response
// waiter armed per Logout click. Provider specifics live in the derived fixtures.
public abstract class BrowserSmokeTestBase
{
    protected const int ActionTimeoutMs = 15_000;
    protected const int NavTimeoutMs = 40_000;
    protected const int StageTimeoutMs = 25_000;
    protected const int TotalBudgetMs = 180_000;

    protected const string OidcButton = "button.oidc-button";
    protected const string LogoutControl =
        "form.logout-action-form button[type='submit'], form.logout-menu-form button[type='submit']";
    protected const string ConnectionDialog = ".connection-dialog-content";
    protected const string DialogCancel = ".mud-dialog .connection-dialog-content button:has-text('Cancel')";

    private static readonly string[] _hostResolverArgs =
    [
        "--host-resolver-rules=MAP authentik.localhost 127.0.0.1,MAP keycloak.localhost 127.0.0.1",
    ];

    private string _stage = "startup";
    private string _appOrigin = "https://localhost:5001";

    // Provider contract: how to sign in, and exactly which logout endpoint must show up on
    // the wire as an actual browser document response.
    protected abstract Task SignInAsync(IPage page, string username, string password, string tag);

    protected abstract string EndSessionOrigin { get; }

    protected abstract string EndSessionPath { get; }

    protected abstract int EndSessionMinStatus { get; }

    protected string AppOrigin => _appOrigin;

    protected void SetStage(string stage) => _stage = stage;

    // Default base-URL rules (absolute https loopback, bare root origin); the Keycloak lab
    // overrides this to pin the exact redirect-allowlist URL.
    protected virtual string ResolveBaseUrl()
    {
        var baseUrl = (Environment.GetEnvironmentVariable("MQTTPROBE_TEST_BASE_URL") ?? "https://localhost:5001")
            .TrimEnd('/');
        Assert.That(Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri), Is.True,
            "MQTTPROBE_TEST_BASE_URL must be an absolute URL");
        Assert.That(baseUri!.Scheme, Is.EqualTo(Uri.UriSchemeHttps), "the lab base URL must be https");
        Assert.That(baseUri.IsLoopback, Is.True, "the lab base URL must be a loopback host");

        // Fixed message: user info or a query string must never appear in failure output.
        if (baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0 ||
            baseUri.AbsolutePath != "/")
        {
            Assert.Fail("MQTTPROBE_TEST_BASE_URL must be a bare root origin " +
                "(no user info, query string, fragment, or path)");
        }

        return baseUri.GetLeftPart(UriPartial.Authority);
    }

    protected async Task RunSmokeAsync(CancellationToken cancellationToken)
    {
        var username = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_USERNAME")?.Trim();
        var password = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_PASSWORD");
        Assert.That(username, Is.Not.Null.And.Not.Empty,
            "MQTTPROBE_TEST_USERNAME is not set; export it (see README.md)");
        Assert.That(password, Is.Not.Null.And.Not.Empty,
            "MQTTPROBE_TEST_PASSWORD is not set; export it (see README.md)");
        _appOrigin = ResolveBaseUrl();

        IPlaywright? playwright = null;
        IBrowser? browser = null;

        try
        {
            SetStage("browser launch");
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false, // manual run: the flow is meant to be watched
                Args = _hostResolverArgs,
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(ActionTimeoutMs);
            page.SetDefaultNavigationTimeout(NavTimeoutMs);

            // Hard overall bound: the scenario must finish inside TotalBudgetMs.
            var scenario = ScenarioAsync(page, username!, password!);
            if (await Task.WhenAny(scenario, Task.Delay(TotalBudgetMs, cancellationToken)) != scenario)
            {
                Assert.Fail($"[overall] scenario exceeded {TotalBudgetMs}ms");
            }

            await scenario;
        }
        catch (AssertionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Boundary: stage plus exception type only; Playwright messages can embed URLs.
            Assert.Fail($"[{_stage}] failed with {ex.GetType().Name}");
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync();
                }
                catch (Exception)
                {
                    // Browser teardown is best effort.
                }
            }

            playwright?.Dispose();
        }
    }

    private async Task ScenarioAsync(IPage page, string username, string password)
    {
        SetStage("first visit to /Login");
        await page.GotoAsync($"{_appOrigin}/Login", new PageGotoOptions
        {
            Timeout = NavTimeoutMs,
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });

        await SignInAsync(page, username, password, "first sign-in");
        await LogoutAsync(page, "first logout");

        // Same context and page: both credential prompts must come back (no silent SSO).
        await SignInAsync(page, username, password, "second sign-in");
        await LogoutAsync(page, "final logout");
    }

    private async Task LogoutAsync(IPage page, string tag)
    {
        SetStage($"{tag}: app shell");
        await page.Locator(LogoutControl).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs });

        SetStage($"{tag}: close connection dialog");
        var dialog = page.Locator(ConnectionDialog).First;
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs });
        await page.Locator(DialogCancel).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = ActionTimeoutMs });

        SetStage($"{tag}: end-session request");
        var expected = new Uri(EndSessionOrigin);
        var endSession = page.WaitForResponseAsync(
            IsEndSessionDocument,
            new PageWaitForResponseOptions { Timeout = NavTimeoutMs });
        await page.Locator(LogoutControl).First.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs });
        await endSession;

        SetStage($"{tag}: final /Login");
        await page.Locator(OidcButton).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs });
        AssertOrigin(page, _appOrigin, tag);
        var pageUrl = page.MainFrame?.Url ?? string.Empty;
        Assert.That(Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) && pageUri.AbsolutePath == "/Login",
            Is.True, $"[{tag}] expected the app /Login page after logout");

        // Exact parsed origin plus path plus acceptable status; a new waiter per Logout click.
        bool IsEndSessionDocument(IResponse response)
        {
            var request = response.Request;
            if (!string.Equals(request.ResourceType, "document", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(request.Url, UriKind.Absolute, out var docUri))
            {
                return false;
            }

            return docUri.Scheme == Uri.UriSchemeHttps
                && docUri.Host == expected.Host
                && docUri.Port == expected.Port
                && string.Equals(docUri.AbsolutePath, EndSessionPath, StringComparison.Ordinal)
                && response.Status >= EndSessionMinStatus
                && response.Status < 400;
        }
    }

    // Fail closed: credentials are typed only when the browser's parsed origin matches exactly.
    protected static void AssertOrigin(IPage page, string expectedOrigin, string tag)
    {
        var url = page.MainFrame?.Url ?? string.Empty;
        var origin = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "(unparseable URL)";
        Assert.That(origin, Is.EqualTo(expectedOrigin),
            $"[{tag}] unexpected page origin (expected {expectedOrigin}); refusing to continue");
    }
}
