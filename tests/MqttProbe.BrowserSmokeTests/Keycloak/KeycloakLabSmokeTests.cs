using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

[TestFixture]
public sealed class KeycloakLabSmokeTests : BrowserSmokeTestBase
{
    private const string IdpOrigin = "https://keycloak.localhost:8443";
    private const string UsernameField = "input[name='username']";
    private const string PasswordField = "input[name='password']";
    private const string LoginSubmit = "#kc-login";

    protected override string EndSessionOrigin => IdpOrigin;

    protected override string EndSessionPath => "/realms/mqttprobe-test/protocol/openid-connect/logout";

    // Keycloak's RP-initiated logout redirects when a post-logout URL is configured and may
    // render its own logged-out page otherwise, so both 3xx and 2xx count.
    protected override int EndSessionMinStatus => 200;

    // The Keycloak lab registers exactly this URL in the client's redirect allowlist.
    protected override string ResolveBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("MQTTPROBE_TEST_BASE_URL");
        if (configured is not null && configured.TrimEnd('/') != "https://localhost:5001")
        {
            Assert.Fail("MQTTPROBE_TEST_BASE_URL must be exactly https://localhost:5001 for the Keycloak lab");
        }

        return "https://localhost:5001";
    }

    [Test]
    [CancelAfter(TotalBudgetMs + 30_000)]
    public Task KeycloakLoginLogoutReLoginRequestsRealEndSession(CancellationToken cancellationToken)
        => RunSmokeAsync(cancellationToken);

    protected override async Task SignInAsync(IPage page, string username, string password, string tag)
    {
        SetStage($"{tag}: OIDC sign-in button");
        var signIn = page.Locator(OidcButton).First;
        await signIn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs });

        // Fail early when the wrong lab answers: both labs share https://localhost:5001.
        var label = await signIn.InnerTextAsync();
        if (!label.Contains("keycloak", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"[{tag}] the /Login button is not the Keycloak entry point (wrong lab is running)");
        }

        await signIn.ClickAsync();

        SetStage($"{tag}: username and password form");
        var user = page.Locator(UsernameField).First;
        var secret = page.Locator(PasswordField).First;
        await user.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StageTimeoutMs });
        await secret.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StageTimeoutMs });
        AssertOrigin(page, IdpOrigin, tag);
        await user.FillAsync(username);
        await secret.FillAsync(password);
        await page.Locator(LoginSubmit).First.ClickAsync();

        SetStage($"{tag}: authenticated app shell");
        await page.Locator(LogoutControl).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs });
        AssertOrigin(page, AppOrigin, tag);
    }
}
