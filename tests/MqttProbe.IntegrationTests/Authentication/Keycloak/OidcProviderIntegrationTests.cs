using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.TestInfrastructure.Fixtures;
using MqttProbe.Web.Authentication;

namespace MqttProbe.IntegrationTests.Authentication;

[TestFixture]
[NonParallelizable]
public class OidcProviderIntegrationTests : IAsyncDisposable
{
    private KeycloakFixture? _fixture;

    [OneTimeSetUp]
    public async Task StartKeycloak()
    {
        _fixture = await KeycloakFixture.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_fixture is not null)
            await _fixture.DisposeAsync();
    }

    [Test]
    public async Task AuthorizationCodePkce_AdmittedUserGetsAppSession()
    {
        var (factory, appClient, driver) = CreateAppClientAndDriver();

        await using (factory)
        using (appClient)
        using (driver)
        {
            var authorizationUri = await driver.BeginLoginAsync("/");

            var correlationCookies = driver.ChallengeSetCookieHeaders
                .Where(h => h.StartsWith(".AspNetCore.Correlation.", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var nonceCookies = driver.ChallengeSetCookieHeaders
                .Where(h => h.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.OrdinalIgnoreCase))
                .ToList();

            correlationCookies.Should().NotBeEmpty("challenge must set a correlation cookie");
            nonceCookies.Should().NotBeEmpty("challenge must set a nonce cookie");

            foreach (var header in correlationCookies.Concat(nonceCookies))
            {
                var cookieName = header.Split(';')[0].Split('=')[0];
                var attributes = header.Split(';', StringSplitOptions.TrimEntries);

                attributes.Should().Contain(a => string.Equals(a, "secure", StringComparison.OrdinalIgnoreCase),
                    $"{cookieName} must have Secure");
                var sameSite = attributes.FirstOrDefault(a =>
                    a.StartsWith("samesite=", StringComparison.OrdinalIgnoreCase));
                sameSite.Should().NotBeNull($"{cookieName} must have SameSite");
                string.Equals(sameSite!.Split('=', 2).Last(), "None", StringComparison.OrdinalIgnoreCase)
                    .Should().BeTrue($"{cookieName} must have SameSite=None");
            }

            var query = HttpUtility.ParseQueryString(authorizationUri.Query);

            authorizationUri.GetLeftPart(UriPartial.Path).Should().Be(
                $"{_fixture!.Authority}/protocol/openid-connect/auth");

            // With PAR, sensitive parameters are pushed to the back-channel.
            // The front-channel URL carries only client_id and request_uri.
            query.Get("client_id").Should().Be(KeycloakFixture.ClientId);
            query.Get("request_uri").Should().NotBeNullOrWhiteSpace(
                "PAR must produce a request_uri on the front-channel URL");

            query.Get("response_type").Should().BeNull(
                "response_type must not be exposed in the front-channel URL with PAR");
            query.Get("redirect_uri").Should().BeNull(
                "redirect_uri must not be exposed in the front-channel URL with PAR");
            query.Get("code_challenge").Should().BeNull(
                "code_challenge must not be exposed in the front-channel URL with PAR");

            var callback = await driver.SubmitProviderLoginAsync(
                authorizationUri,
                KeycloakFixture.AdmittedUsername,
                KeycloakFixture.Password);

            callback.Method.Should().Be(OidcCallbackMethod.PostForm,
                "Keycloak must return a form_post response");
            callback.TargetUri.Host.Should().Be("mqttprobe.test");
            callback.TargetUri.AbsolutePath.Should().Be("/signin-oidc");

            callback.FormFields.Should().Contain(f => f.Key == "code" && !string.IsNullOrEmpty(f.Value),
                "form_post must include a non-empty code");
            callback.FormFields.Should().Contain(f => f.Key == "state" && !string.IsNullOrEmpty(f.Value),
                "form_post must include a non-empty state");

            using var callbackResponse = await driver.SendAppCallbackAsync(callback);

            var appCookieName = ".AspNetCore.Cookies";
            callbackResponse.Headers.TryGetValues("Set-Cookie", out var callbackCookies).Should().BeTrue(
                "callback response must set the app auth cookie");

            var appCookieHeader = callbackCookies!.FirstOrDefault(h =>
                h.StartsWith(appCookieName + "=", StringComparison.OrdinalIgnoreCase));
            appCookieHeader.Should().NotBeNull(
                $"callback response must set the '{appCookieName}' cookie");

            var appAttributes = appCookieHeader!.Split(';', StringSplitOptions.TrimEntries);
            appAttributes.Should().Contain(a => string.Equals(a, "secure", StringComparison.OrdinalIgnoreCase),
                "app cookie must have Secure");
            appAttributes.Should().Contain(a => string.Equals(a, "httponly", StringComparison.OrdinalIgnoreCase),
                "app cookie must have HttpOnly");
            var appSameSite = appAttributes.FirstOrDefault(a =>
                a.StartsWith("samesite=", StringComparison.OrdinalIgnoreCase));
            appSameSite.Should().NotBeNull("app cookie must have SameSite");
            string.Equals(appSameSite!.Split('=', 2).Last(), "Lax", StringComparison.OrdinalIgnoreCase)
                .Should().BeTrue("app cookie must have SameSite=Lax");

            callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var postCallbackLocation = callbackResponse.Headers.Location;
            postCallbackLocation.Should().NotBeNull();
            postCallbackLocation!.IsAbsoluteUri.Should().BeFalse();
            postCallbackLocation.OriginalString.Should().Be("/");

            using var followResponse = await appClient.GetAsync(postCallbackLocation.OriginalString);
            followResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            factory.Services.GetService<AppSessionCoordinator>().Should().NotBeNull();
        }
    }

    [Test]
    public async Task AuthorizationCodePkce_DeniedUserGetsAccessDeniedWithoutAppSession()
    {
        var (factory, appClient, driver) = CreateAppClientAndDriver();

        await using (factory)
        using (appClient)
        using (driver)
        {
            var authorizationUri = await driver.BeginLoginAsync("/");

            var callback = await driver.SubmitProviderLoginAsync(
                authorizationUri,
                KeycloakFixture.DeniedUsername,
                KeycloakFixture.Password);

            callback.Method.Should().Be(OidcCallbackMethod.PostForm,
                "Keycloak must return a form_post response");
            callback.TargetUri.Host.Should().Be("mqttprobe.test");
            callback.TargetUri.AbsolutePath.Should().Be("/signin-oidc");

            using var callbackResponse = await driver.SendAppCallbackAsync(callback);

            if (callbackResponse.Headers.TryGetValues("Set-Cookie", out var deniedCookies))
            {
                var appCookiePrefix = ".AspNetCore.Cookies=";
                var issuedAppCookie = deniedCookies.FirstOrDefault(h =>
                    h.StartsWith(appCookiePrefix, StringComparison.OrdinalIgnoreCase));

                if (issuedAppCookie is not null)
                {
                    var attributes = issuedAppCookie.Split(';', StringSplitOptions.TrimEntries);
                    var isDeletion = attributes.Any(a =>
                        string.Equals(a, "max-age=0", StringComparison.OrdinalIgnoreCase) ||
                        (a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase) &&
                         a.Contains("1970", StringComparison.OrdinalIgnoreCase)));

                    isDeletion.Should().BeTrue(
                        "denied callback must not issue a non-expired app auth cookie");
                }
            }

            callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var denialLocation = callbackResponse.Headers.Location;
            denialLocation.Should().NotBeNull();
            denialLocation!.IsAbsoluteUri.Should().BeFalse();

            var resolvedDenial = ResolveLocation(denialLocation, appClient.BaseAddress!);
            resolvedDenial.AbsolutePath.Should().Be("/AccessDenied");

            var denialQuery = HttpUtility.ParseQueryString(resolvedDenial.Query);
            var state = denialQuery.Get("state");
            state.Should().NotBeNullOrWhiteSpace();

            using var denialPageResponse = await appClient.GetAsync(denialLocation.OriginalString);
            denialPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var denialHtml = await denialPageResponse.Content.ReadAsStringAsync();
            denialHtml.Should().Contain("Access not authorized");
            denialHtml.Should().Contain("Keycloak");
            var document = new HtmlParser().ParseDocument(denialHtml);

            // Verify the internal admission claim name is not leaked.
            denialHtml.Should().NotContain(KeycloakFixture.AdmissionClaim,
                "the internal claim name must not appear in the denial page");

            // Verify the accepted claim value is not rendered as a standalone token.
            // Use word-boundary match so normal prose like "administrator" is not a false positive.
            var claimValuePattern = $@"\b{Regex.Escape(KeycloakFixture.AcceptedValue)}\b";
            Regex.IsMatch(denialHtml, claimValuePattern, RegexOptions.IgnoreCase).Should().BeFalse(
                "the accepted claim value must not appear as a standalone token in the denial page");

            var displayName = document.QuerySelector(".denial-detail strong")?.TextContent;
            displayName.Should().NotBeNullOrWhiteSpace();

            using var protectedResponse = await appClient.GetAsync("/");
            protectedResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var loginRedirect = protectedResponse.Headers.Location;
            loginRedirect.Should().NotBeNull();

            var resolvedLogin = ResolveLocation(loginRedirect, appClient.BaseAddress!);
            resolvedLogin.AbsolutePath.Should().Be("/Login");

            factory.Services.GetService<AppSessionCoordinator>().Should().NotBeNull();
        }
    }

    [Test]
    public async Task ProviderLogout_UsesExpectedUrlAndClearsLocalSessionFirst()
    {
        var (factory, appClient, driver) = CreateAppClientAndDriver();

        await using (factory)
        using (appClient)
        using (driver)
        {
            await AuthenticateAdmittedAsync(appClient, driver);

            var logoutUri = await driver.BeginLogoutAsync();

            logoutUri.GetLeftPart(UriPartial.Path).Should().Be(
                $"{_fixture!.Authority}/protocol/openid-connect/logout");

            var query = HttpUtility.ParseQueryString(logoutUri.Query);

            query.Get("id_token_hint").Should().NotBeNullOrWhiteSpace();
            query.Get("post_logout_redirect_uri").Should().Be(
                "https://mqttprobe.test/signout-callback-oidc");

            var clientId = query.Get("client_id");
            if (clientId is not null)
                clientId.Should().Be(KeycloakFixture.ClientId);

            using var protectedResponse = await appClient.GetAsync("/");
            protectedResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var loginRedirect = protectedResponse.Headers.Location;
            loginRedirect.Should().NotBeNull();

            var resolvedLogin = ResolveLocation(loginRedirect, appClient.BaseAddress!);
            resolvedLogin.AbsolutePath.Should().Be("/Login");

            using var providerLogoutResponse = await driver.ProviderClient.GetAsync(logoutUri);
            providerLogoutResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var providerLogoutLocation = providerLogoutResponse.Headers.Location;
            providerLogoutLocation.Should().NotBeNull();
            providerLogoutLocation!.IsAbsoluteUri.Should().BeTrue();
            providerLogoutLocation.UserInfo.Should().BeEmpty();
            providerLogoutLocation.Scheme.Should().Be("https");
            providerLogoutLocation.Host.Should().Be("mqttprobe.test");
            providerLogoutLocation.Port.Should().Be(443);
            providerLogoutLocation.AbsolutePath.Should().Be("/signout-callback-oidc");

            using var signoutCallbackResponse = await appClient.GetAsync(providerLogoutLocation.PathAndQuery);
            signoutCallbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var signoutCallbackLocation = signoutCallbackResponse.Headers.Location;
            signoutCallbackLocation.Should().NotBeNull();
            signoutCallbackLocation!.OriginalString.Should().Be("/Login");

            var postLogoutAuthorizationUri = await driver.BeginLoginAsync("/");

            using var providerLoginResponse = await driver.ProviderClient.GetAsync(postLogoutAuthorizationUri);
            providerLoginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var providerLoginHtml = await providerLoginResponse.Content.ReadAsStringAsync();
            var providerLoginDocument = new HtmlParser().ParseDocument(providerLoginHtml);

            var credentialForm = providerLoginDocument.QuerySelectorAll("form")
                .FirstOrDefault(f =>
                    f.QuerySelector("input[name='username']:not([disabled])") is not null &&
                    f.QuerySelector("input[name='password'][type='password']:not([disabled])") is not null);

            credentialForm.Should().NotBeNull();
        }
    }

    [Test]
    public async Task DeniedUser_LogoutFirst_CanSwitchToAdmittedUser()
    {
        var (factory, appClient, driver) = CreateAppClientAndDriver();

        await using (factory)
        using (appClient)
        using (driver)
        {
            // Login as denied user
            var authorizationUri = await driver.BeginLoginAsync("/");

            var callback = await driver.SubmitProviderLoginAsync(
                authorizationUri,
                KeycloakFixture.DeniedUsername,
                KeycloakFixture.Password);

            using var callbackResponse = await driver.SendAppCallbackAsync(callback);

            // Should redirect to AccessDenied
            callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var denialLocation = callbackResponse.Headers.Location;
            denialLocation.Should().NotBeNull();

            var resolvedDenial = ResolveLocation(denialLocation, appClient.BaseAddress!);
            resolvedDenial.AbsolutePath.Should().Be("/AccessDenied");

            // Get the AccessDenied page
            using var denialPageResponse = await appClient.GetAsync(denialLocation!.OriginalString);
            denialPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var denialHtml = await denialPageResponse.Content.ReadAsStringAsync();
            var document = new HtmlParser().ParseDocument(denialHtml);

            // Find the primary "Use another account" form (first form.action*='/Logout')
            var logoutForms = document.QuerySelectorAll("form[action*='/Logout']");
            logoutForms.Length.Should().BeGreaterThanOrEqualTo(1,
                "AccessDenied page must contain at least one logout form");

            var primaryForm = logoutForms[0];

            // Submit the primary AccessDenied form (POST to /Logout)
            var formData = new List<KeyValuePair<string, string>>();
            foreach (var input in primaryForm.QuerySelectorAll("input"))
            {
                if (input.HasAttribute("disabled"))
                    continue;

                var name = input.GetAttribute("name");
                if (string.IsNullOrEmpty(name))
                    continue;

                formData.Add(new KeyValuePair<string, string>(name, input.GetAttribute("value") ?? ""));
            }

            var action = primaryForm.GetAttribute("action");
            action.Should().NotBeNullOrWhiteSpace();

            using var logoutContent = new FormUrlEncodedContent(formData);
            using var logoutResponse = await appClient.PostAsync(action, logoutContent);

            // Should redirect to provider logout
            logoutResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var logoutRedirect = logoutResponse.Headers.Location;
            logoutRedirect.Should().NotBeNull();

            var logoutQuery = HttpUtility.ParseQueryString(logoutRedirect!.Query);

            // Denied user has no id_token, so must use client_id instead
            logoutQuery.Get("id_token_hint").Should().BeNull(
                "denied user logout must not have id_token_hint");
            logoutQuery.Get("client_id").Should().Be(KeycloakFixture.ClientId,
                "denied user logout must include client_id");
            logoutQuery.Get("post_logout_redirect_uri").Should().Be(
                "https://mqttprobe.test/signout-callback-oidc");

            // Complete Keycloak confirmation via strict driver helper
            using var providerLogoutResponse = await driver.SubmitProviderConfirmationAsync(logoutRedirect);

            ((int)providerLogoutResponse.StatusCode).Should().BeInRange(200, 399,
                "provider must accept the logout request");

            // Follow trusted signout callback to Login
            if (providerLogoutResponse.StatusCode == HttpStatusCode.Redirect)
            {
                var providerLogoutLocation = providerLogoutResponse.Headers.Location;
                providerLogoutLocation.Should().NotBeNull();
                providerLogoutLocation!.IsAbsoluteUri.Should().BeTrue();
                providerLogoutLocation.Host.Should().Be("mqttprobe.test");
                providerLogoutLocation.AbsolutePath.Should().Be("/signout-callback-oidc");

                using var signoutCallbackResponse = await appClient.GetAsync(providerLogoutLocation.PathAndQuery);
                signoutCallbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
                    "signout callback must redirect");
                var signoutCallbackLocation = signoutCallbackResponse.Headers.Location;
                signoutCallbackLocation.Should().NotBeNull();
                signoutCallbackLocation!.OriginalString.Should().Be("/Login",
                    "signout callback must redirect to Login");
            }
            else
            {
                // Keycloak showed a confirmation page - submit it
                var confirmHtml = await providerLogoutResponse.Content.ReadAsStringAsync();
                var confirmDoc = new HtmlParser().ParseDocument(confirmHtml);

                var confirmForm = confirmDoc.QuerySelectorAll("form")
                    .FirstOrDefault(f => f.QuerySelector("input[type='submit'], button[type='submit']") is not null);

                if (confirmForm is not null)
                {
                    var confirmData = CollectFormInputs(confirmForm);
                    var confirmAction = confirmForm.GetAttribute("action");
                    if (string.IsNullOrEmpty(confirmAction))
                        confirmAction = logoutRedirect.ToString();
                    else if (!confirmAction.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        confirmAction = new Uri(logoutRedirect, confirmAction).ToString();

                    using var confirmContent = new FormUrlEncodedContent(confirmData);
                    using var confirmResponse = await driver.ProviderClient.PostAsync(confirmAction, confirmContent);

                    if (confirmResponse.StatusCode == HttpStatusCode.Redirect)
                    {
                        var confirmLocation = confirmResponse.Headers.Location;
                        confirmLocation.Should().NotBeNull();

                        if (confirmLocation!.IsAbsoluteUri &&
                            confirmLocation.Host == "mqttprobe.test")
                        {
                            using var signoutCallbackResponse = await appClient.GetAsync(confirmLocation.PathAndQuery);
                            signoutCallbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
                            var signoutCallbackLocation = signoutCallbackResponse.Headers.Location;
                            signoutCallbackLocation.Should().NotBeNull();
                            signoutCallbackLocation!.OriginalString.Should().Be("/Login");
                        }
                    }
                }
            }

            // Start a normal challenge (no forceLogin, no prompt=login)
            var freshAuthUri = await driver.BeginLoginAsync("/");

            // Submit admitted credentials using existing strict login helper.
            // SubmitProviderLoginAsync internally GETs the provider page, finds the
            // credential form (username+password inputs), fills in credentials, and POSTs.
            // If SSO were still active, Keycloak would silently redirect instead of
            // presenting a credential form, and FindCredentialForm would throw.
            var admittedCallback = await driver.SubmitProviderLoginAsync(
                freshAuthUri,
                KeycloakFixture.AdmittedUsername,
                KeycloakFixture.Password);

            admittedCallback.Method.Should().Be(OidcCallbackMethod.PostForm,
                "Keycloak must return a form_post response");
            admittedCallback.TargetUri.Host.Should().Be("mqttprobe.test");
            admittedCallback.TargetUri.AbsolutePath.Should().Be("/signin-oidc");

            // Complete form_post callback
            using var admittedCallbackResponse = await driver.SendAppCallbackAsync(admittedCallback);

            // Require Location /
            admittedCallbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
                "admitted callback must redirect");
            var postCallbackLocation = admittedCallbackResponse.Headers.Location;
            postCallbackLocation.Should().NotBeNull();
            postCallbackLocation!.OriginalString.Should().Be("/",
                "admitted callback must redirect to the return URL");

            // GET protected / returns 200
            using var homeResponse = await appClient.GetAsync("/");
            homeResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "admitted user must be able to access protected resources");
        }
    }

    [Test]
    public async Task DeniedUser_AnonymousLogout_UsesClientIdWithoutIdToken()
    {
        var (factory, appClient, driver) = CreateAppClientAndDriver();

        await using (factory)
        using (appClient)
        using (driver)
        {
            // Login as denied user
            var authorizationUri = await driver.BeginLoginAsync("/");

            var callback = await driver.SubmitProviderLoginAsync(
                authorizationUri,
                KeycloakFixture.DeniedUsername,
                KeycloakFixture.Password);

            using var callbackResponse = await driver.SendAppCallbackAsync(callback);

            // Should redirect to AccessDenied
            callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var denialLocation = callbackResponse.Headers.Location;
            denialLocation.Should().NotBeNull();

            // Get the AccessDenied page
            using var denialPageResponse = await appClient.GetAsync(denialLocation!.OriginalString);
            denialPageResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var denialHtml = await denialPageResponse.Content.ReadAsStringAsync();
            var document = new HtmlParser().ParseDocument(denialHtml);

            // Find the logout form
            var logoutForm = document.QuerySelector("form[action*='/Logout']");
            logoutForm.Should().NotBeNull("AccessDenied page must contain a logout form");

            // Submit the logout form
            var formData = new List<KeyValuePair<string, string>>();
            foreach (var input in logoutForm!.QuerySelectorAll("input"))
            {
                if (input.HasAttribute("disabled"))
                    continue;

                var name = input.GetAttribute("name");
                if (string.IsNullOrEmpty(name))
                    continue;

                formData.Add(new KeyValuePair<string, string>(name, input.GetAttribute("value") ?? ""));
            }

            var action = logoutForm.GetAttribute("action");
            action.Should().NotBeNullOrWhiteSpace();

            using var logoutContent = new FormUrlEncodedContent(formData);
            using var logoutResponse = await appClient.PostAsync(action, logoutContent);

            // Should redirect to provider logout
            logoutResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
            var logoutRedirect = logoutResponse.Headers.Location;
            logoutRedirect.Should().NotBeNull();

            var logoutQuery = HttpUtility.ParseQueryString(logoutRedirect!.Query);

            // Denied user has no id_token, so should use client_id instead
            logoutQuery.Get("id_token_hint").Should().BeNull(
                "denied user logout must not have id_token_hint");
            logoutQuery.Get("client_id").Should().Be(KeycloakFixture.ClientId,
                "denied user logout must include client_id");
            logoutQuery.Get("post_logout_redirect_uri").Should().Be(
                "https://mqttprobe.test/signout-callback-oidc");

            // Follow Keycloak's logout flow. Keycloak may show a confirmation page
            // or redirect directly. SubmitProviderConfirmationAsync handles both.
            using var providerLogoutResponse = await driver.SubmitProviderConfirmationAsync(logoutRedirect);

            // Keycloak must accept the logout request (200 confirmation or 302 redirect)
            ((int)providerLogoutResponse.StatusCode).Should().BeInRange(200, 399,
                "provider must accept the logout request");

            // Follow the post-logout callback if Keycloak redirected
            if (providerLogoutResponse.StatusCode == HttpStatusCode.Redirect)
            {
                var providerLogoutLocation = providerLogoutResponse.Headers.Location;
                providerLogoutLocation.Should().NotBeNull();
                providerLogoutLocation!.IsAbsoluteUri.Should().BeTrue();
                providerLogoutLocation.Host.Should().Be("mqttprobe.test");
                providerLogoutLocation.AbsolutePath.Should().Be("/signout-callback-oidc");

                // Follow the signout callback
                using var signoutCallbackResponse = await appClient.GetAsync(providerLogoutLocation.PathAndQuery);
                signoutCallbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect,
                    "signout callback must redirect");
                var signoutCallbackLocation = signoutCallbackResponse.Headers.Location;
                signoutCallbackLocation.Should().NotBeNull();
                signoutCallbackLocation!.OriginalString.Should().Be("/Login",
                    "signout callback must redirect to Login");
            }
            else
            {
                // Keycloak showed a confirmation page - submit it
                var confirmHtml = await providerLogoutResponse.Content.ReadAsStringAsync();
                var confirmDoc = new HtmlParser().ParseDocument(confirmHtml);

                var confirmForm = confirmDoc.QuerySelectorAll("form")
                    .FirstOrDefault(f => f.QuerySelector("input[type='submit'], button[type='submit']") is not null);

                if (confirmForm is not null)
                {
                    var confirmData = CollectFormInputs(confirmForm);
                    var confirmAction = confirmForm.GetAttribute("action");
                    if (string.IsNullOrEmpty(confirmAction))
                        confirmAction = logoutRedirect.ToString();
                    else if (!confirmAction.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        confirmAction = new Uri(logoutRedirect, confirmAction).ToString();

                    using var confirmContent = new FormUrlEncodedContent(confirmData);
                    using var confirmResponse = await driver.ProviderClient.PostAsync(confirmAction, confirmContent);

                    if (confirmResponse.StatusCode == HttpStatusCode.Redirect)
                    {
                        var confirmLocation = confirmResponse.Headers.Location;
                        confirmLocation.Should().NotBeNull();

                        if (confirmLocation!.IsAbsoluteUri &&
                            confirmLocation.Host == "mqttprobe.test")
                        {
                            using var signoutCallbackResponse = await appClient.GetAsync(confirmLocation.PathAndQuery);
                            signoutCallbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
                            var signoutCallbackLocation = signoutCallbackResponse.Headers.Location;
                            signoutCallbackLocation.Should().NotBeNull();
                            signoutCallbackLocation!.OriginalString.Should().Be("/Login");
                        }
                    }
                }
            }

            // Prove a fresh authorization request presents login rather than silently reusing denied SSO
            var freshAuthUri = await driver.BeginLoginAsync("/");
            using var freshProviderResponse = await driver.ProviderClient.GetAsync(freshAuthUri);
            freshProviderResponse.StatusCode.Should().Be(HttpStatusCode.OK,
                "provider must present a login form for fresh authorization");

            var freshHtml = await freshProviderResponse.Content.ReadAsStringAsync();
            var freshDoc = new HtmlParser().ParseDocument(freshHtml);

            var freshCredentialForm = freshDoc.QuerySelectorAll("form")
                .FirstOrDefault(f =>
                    f.QuerySelector("input[name='username']:not([disabled])") is not null &&
                    f.QuerySelector("input[name='password'][type='password']:not([disabled])") is not null);

            freshCredentialForm.Should().NotBeNull(
                "provider must present a credential form for fresh authorization after logout, " +
                "not silently reuse the denied SSO session");
        }
    }

    private static List<KeyValuePair<string, string>> CollectFormInputs(AngleSharp.Dom.IElement form)
    {
        var data = new List<KeyValuePair<string, string>>();
        foreach (var input in form.QuerySelectorAll("input"))
        {
            if (input.HasAttribute("disabled"))
                continue;

            var name = input.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;

            data.Add(new KeyValuePair<string, string>(name, input.GetAttribute("value") ?? ""));
        }
        return data;
    }

    private async Task AuthenticateAdmittedAsync(
        HttpClient appClient, OidcProtocolDriver driver)
    {
        var authorizationUri = await driver.BeginLoginAsync("/");

        var callback = await driver.SubmitProviderLoginAsync(
            authorizationUri,
            KeycloakFixture.AdmittedUsername,
            KeycloakFixture.Password);

        using var callbackResponse = await driver.SendAppCallbackAsync(callback);

        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var postCallbackLocation = callbackResponse.Headers.Location;
        postCallbackLocation.Should().NotBeNull();
        postCallbackLocation!.IsAbsoluteUri.Should().BeFalse();
        postCallbackLocation.OriginalString.Should().Be("/");

        using var followResponse = await appClient.GetAsync(postCallbackLocation.OriginalString);
        followResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private (OidcWebApplicationFactory factory, HttpClient appClient, OidcProtocolDriver driver) CreateAppClientAndDriver()
    {
        var factory = new OidcWebApplicationFactory(
            _fixture!.Authority,
            KeycloakFixture.ClientId,
            KeycloakFixture.ClientSecret,
            KeycloakFixture.AdmissionClaim,
            KeycloakFixture.AcceptedValue);

        var appClient = factory.CreateOidcClient();
        var driver = new OidcProtocolDriver(appClient);

        return (factory, appClient, driver);
    }

    private static Uri ResolveLocation(Uri? location, Uri baseAddress)
    {
        location.Should().NotBeNull("Location header must be present");

        if (location!.IsAbsoluteUri)
        {
            location.Scheme.Should().Be(baseAddress.Scheme, "Location must target the app origin");
            location.Host.Should().Be(baseAddress.Host, "Location must target the app origin");
            location.Port.Should().Be(baseAddress.Port, "Location must target the app origin");
            return location;
        }

        return new Uri(baseAddress, location);
    }
}
