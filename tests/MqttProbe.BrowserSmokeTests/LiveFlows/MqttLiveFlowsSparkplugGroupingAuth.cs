using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

// Grouping-only auth: one visible sign-in per fixture run, then in-memory cookie reuse. Each case
// still gets a fresh browser context and nothing is persisted to disk.
public sealed partial class MqttLiveFlowsSmokeTests
{
    // Default ASP.NET Core cookie-auth name plus numeric chunk suffixes; the app does not rename it.
    private const string AuthCookieName = ".AspNetCore.Cookies";

    // Fixture-scoped in-memory session; cleared at fixture teardown.
    private GroupingAuthSession? _groupingAuthSession;

    private sealed record GroupingAuthSession(string Origin, IReadOnlyList<Cookie> Cookies);

    [OneTimeTearDown]
    public void ClearGroupingAuthSession() => _groupingAuthSession = null;

    // The first grouping case signs in through the real controls and caches the cookies; later
    // cases restore them into a fresh context. A restore that does not reach the authenticated
    // shell fails instead of signing in again silently.
    private async Task LoginForGroupingAsync(
        IPage page, string appOrigin, string username, string password, CancellationToken token)
    {
        if (_groupingAuthSession is { } session)
        {
            Assert.That(string.Equals(session.Origin, appOrigin, StringComparison.Ordinal), Is.True,
                "cached grouping auth belongs to a different app origin; refusing to reuse it");
            await RestoreGroupingAuthAsync(page, appOrigin, session, token);
            return;
        }

        await LoginWithVisibleControlsAsync(page, appOrigin, username, password, token);
    }

    private async Task RestoreGroupingAuthAsync(
        IPage page, string appOrigin, GroupingAuthSession session, CancellationToken token)
    {
        SetStage("restore grouping auth cookies");
        await page.Context.AddCookiesAsync(session.Cookies).WaitAsync(token);

        await page.GotoAsync($"{appOrigin}/", new()
        {
            Timeout = NavTimeoutMs,
            WaitUntil = WaitUntilState.DOMContentLoaded,
        }).WaitAsync(token);
        AssertOrigin(page, appOrigin);

        var url = page.MainFrame?.Url ?? string.Empty;
        Assert.That(Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !string.Equals(uri.AbsolutePath, "/Login", StringComparison.Ordinal), Is.True,
            "restored grouping auth reached /Login instead of an authenticated page");

        await ExpectAuthenticatedShellAsync(page, token);
    }

    private async Task LoginWithVisibleControlsAsync(
        IPage page, string appOrigin, string username, string password, CancellationToken token)
    {
        SetStage("navigate to /Login");
        await page.GotoAsync($"{appOrigin}/Login", new()
        {
            Timeout = NavTimeoutMs,
            WaitUntil = WaitUntilState.DOMContentLoaded,
        }).WaitAsync(token);
        AssertOrigin(page, appOrigin);

        SetStage("assert a single login form");
        var usernameInput = page.Locator("input#username");
        var passwordInput = page.Locator("input#password");
        var submit = page.Locator("form button[type='submit']:not(.oidc-button)");
        await Assertions.Expect(usernameInput).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);
        await Assertions.Expect(passwordInput).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);
        await Assertions.Expect(submit).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs })
            .WaitAsync(token);

        SetStage("submit credentials");
        var loginResponse = page.WaitForResponseAsync(IsLoginDocument, new() { Timeout = NavTimeoutMs });
        try
        {
            await usernameInput.FillAsync(username, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await passwordInput.FillAsync(password, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
            await submit.ClickAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        }
        catch (PlaywrightException ex)
        {
            // Type only: a Playwright message can echo what was typed into the fields.
            Assert.Fail($"[{_stage}] sign-in controls failed: {ex.GetType().Name}");
        }

        var response = await loginResponse.WaitAsync(token);
        TestContext.Progress.WriteLine($"login response: {DescribeResponse(response)}");

        SetStage("verify login response");
        if (response.Status == 429)
            Assert.Fail($"login rate limited: {DescribeResponse(response)}");

        Assert.That(response.Status, Is.EqualTo(302),
            $"expected the 302 sign-in redirect, saw {DescribeResponse(response)}");

        await ExpectAuthenticatedShellAsync(page, token);

        SetStage("cache grouping auth cookies");
        var result = await page.Context.CookiesAsync([appOrigin]).WaitAsync(token);
        var authCookies = result.Where(IsAuthCookie).Select(CopyCookie).ToList();
        Assert.That(authCookies, Is.Not.Empty,
            "expected the app auth cookie after a successful sign-in");
        _groupingAuthSession = new GroupingAuthSession(appOrigin, authCookies);

        bool IsLoginDocument(IResponse candidate)
        {
            var request = candidate.Request;
            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(request.ResourceType, "document", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri))
                return false;

            return string.Equals(requestUri.GetLeftPart(UriPartial.Authority), appOrigin, StringComparison.Ordinal)
                && string.Equals(requestUri.AbsolutePath, "/Login", StringComparison.Ordinal);
        }
    }

    private async Task ExpectAuthenticatedShellAsync(IPage page, CancellationToken token)
    {
        SetStage("wait for authenticated shell");
        var layout = page.Locator(".mud-layout");
        await Assertions.Expect(layout).ToHaveCountAsync(1, new() { Timeout = NavTimeoutMs }).WaitAsync(token);
        await Assertions.Expect(layout).ToBeVisibleAsync(new() { Timeout = NavTimeoutMs }).WaitAsync(token);

        SetStage("wait for the connection dialog");
        var dialog = page.Locator(ConnectionDialog);
        await Assertions.Expect(dialog).ToHaveCountAsync(1, new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = ActionTimeoutMs }).WaitAsync(token);
    }

    // Only the configured auth cookie and its numeric chunk suffixes (C1, C2, ...).
    private static bool IsAuthCookie(BrowserContextCookiesResult cookie)
    {
        if (!cookie.Name.StartsWith(AuthCookieName, StringComparison.Ordinal))
            return false;

        var suffix = cookie.Name[AuthCookieName.Length..];
        return suffix.Length == 0
            || (suffix.Length > 1 && suffix[0] == 'C' && suffix[1..].All(char.IsAsciiDigit));
    }

    private static Cookie CopyCookie(BrowserContextCookiesResult cookie) => new()
    {
        Name = cookie.Name,
        Value = cookie.Value,
        Domain = cookie.Domain,
        Path = cookie.Path,
        Expires = cookie.Expires,
        HttpOnly = cookie.HttpOnly,
        Secure = cookie.Secure,
        SameSite = cookie.SameSite,
    };

    // Sanitized: method, status, host and path only. No headers, bodies, cookies or query strings.
    private static string DescribeResponse(IResponse response)
    {
        var request = response.Request;
        var target = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            ? $"{uri.Host}{uri.AbsolutePath}"
            : "(unparseable)";
        return $"{request.Method} {response.Status} {target}";
    }
}
