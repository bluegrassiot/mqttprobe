using Microsoft.Playwright;

namespace MqttProbe.BrowserSmokeTests;

[TestFixture]
public sealed class AuthentikLabSmokeTests : BrowserSmokeTestBase
{
    private const string IdpOrigin = "https://authentik.localhost:9443";
    private const string IdentifierField =
        "input#ak-identifier-input:visible, input#id_username:visible, input[name='id_username']:visible";
    private const string PasswordField = "input[type='password']:visible";
    private const string SubmitButton = "button[type='submit']:visible, input[type='submit']:visible";

    protected override string EndSessionOrigin => IdpOrigin;

    protected override string EndSessionPath => "/application/o/mqttprobe/end-session/";

    protected override int EndSessionMinStatus => 300;

    [Test]
    [CancelAfter(TotalBudgetMs + 30_000)]
    public Task LoginLogoutReLoginRequestsRealAuthentikEndSession(CancellationToken cancellationToken)
        => RunSmokeAsync(cancellationToken);

    protected override async Task SignInAsync(IPage page, string username, string password, string tag)
    {
        SetStage($"{tag}: OIDC sign-in button");
        var signIn = page.Locator(OidcButton).First;
        await signIn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = ActionTimeoutMs });
        await signIn.ClickAsync();

        SetStage($"{tag}: username prompt");
        var identifier = page.Locator(IdentifierField).First;
        await identifier.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StageTimeoutMs });
        AssertOrigin(page, IdpOrigin, tag);
        await identifier.FillAsync(username);
        await page.Locator(SubmitButton).First.ClickAsync();

        SetStage($"{tag}: password prompt");
        await identifier.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = StageTimeoutMs });
        var secret = page.Locator(PasswordField).First;
        await secret.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StageTimeoutMs });
        AssertOrigin(page, IdpOrigin, tag);
        await secret.FillAsync(password);
        await page.Locator(SubmitButton).First.ClickAsync();

        SetStage($"{tag}: authenticated app shell");
        await page.Locator(LogoutControl).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = NavTimeoutMs });
        AssertOrigin(page, AppOrigin, tag);
    }
}
