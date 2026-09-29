using System.Net;
using System.Web;

namespace MqttProbe.IntegrationTests.Authentication;

[TestFixture]
public class OidcProtocolDriverTests
{
    [Test]
    public async Task BeginLoginAsync_PreservesChallengeFormAndReturnsProviderRedirect()
    {
        using var handler = new RecordingHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient);

        var result = await driver.BeginLoginAsync("/target");

        handler.GetRequestUri.Should().NotBeNull();
        handler.GetRequestUri!.Query.Should().Contain("returnUrl=%2Ftarget");

        handler.PostRequestUri.Should().NotBeNull();
        var postQuery = handler.PostRequestUri!.Query;
        postQuery.Should().Contain("handler=Challenge");
        postQuery.Should().Contain("opaque=a%2Bb");

        handler.PostBody.Should().NotBeNull();
        var form = HttpUtility.ParseQueryString(handler.PostBody!);
        form.Get("__RequestVerificationToken").Should().Be("token-123");
        form.Get("returnUrl").Should().Be("/target");
        form.Get("extra").Should().Be("value");
        form.Get("disabledField").Should().BeNull();

        result.Should().Be(new Uri("https://idp.test/realms/test/protocol/openid-connect/auth?state=abc"));
    }

    [Test]
    public async Task BeginLoginAsync_CapturesSetCookieHeadersAsImmutableSnapshot()
    {
        using var handler = new RecordingHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient);

        driver.ChallengeSetCookieHeaders.Should().BeEmpty();

        await driver.BeginLoginAsync("/");

        driver.ChallengeSetCookieHeaders.Should().HaveCount(2);
        driver.ChallengeSetCookieHeaders.Should().Contain(h => h.StartsWith(".AspNetCore.Correlation.", StringComparison.OrdinalIgnoreCase));
        driver.ChallengeSetCookieHeaders.Should().Contain(h => h.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.OrdinalIgnoreCase));

        var readOnlyList = driver.ChallengeSetCookieHeaders;
        readOnlyList.Should().BeAssignableTo<IReadOnlyList<string>>();
        driver.ChallengeSetCookieHeaders.Should().BeSameAs(readOnlyList);
    }

    [Test]
    public async Task SubmitProviderLoginAsync_PostsCredentialsAndReturnsCallbackResult()
    {
        var handler = new ProviderRecordingHandler();
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var result = await driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");

        // GET target
        handler.GetRequestUri.Should().Be(authUri);

        // Action query: &amp; decoded, opaque preserved
        handler.PostRequestUri.Should().NotBeNull();
        var postQuery = handler.PostRequestUri!.Query;
        postQuery.Should().Contain("scope=openid");
        postQuery.Should().Contain("state=mystate%2F1");

        // Form body: credentials overwritten, hidden retained, disabled excluded
        var body = handler.PostBody!;
        var form = HttpUtility.ParseQueryString(body);
        form.Get("username").Should().Be("alice");
        form.Get("password").Should().Be("s3cret");
        form.Get("session_code").Should().Be("sc-123");
        form.Get("nonce").Should().Be("n-456");
        form.Get("consent").Should().BeNull();

        // Exactly one occurrence each
        body.Split('&').Count(p => p.StartsWith("username=")).Should().Be(1);
        body.Split('&').Count(p => p.StartsWith("password=")).Should().Be(1);

        // Callback result: 302 redirect produces GET query callback
        result.Method.Should().Be(OidcCallbackMethod.GetQuery);
        result.TargetUri.Should().Be(new Uri("https://mqttprobe.test/signin-oidc?code=abc&state=xyz"));
        result.FormFields.Should().BeEmpty();

        // Driver disposal does not dispose injected provider client
        driver.Dispose();
        var probe = await providerClient.GetAsync("https://idp.test/probe");
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class ProviderRecordingHandler : HttpMessageHandler
    {
        private bool _getDone;

        internal Uri? GetRequestUri { get; private set; }
        internal Uri? PostRequestUri { get; private set; }
        internal string? PostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_getDone)
            {
                _getDone = true;
                GetRequestUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ProviderLoginHtml, System.Text.Encoding.UTF8, "text/html")
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                PostRequestUri = request.RequestUri;
                PostBody = await request.Content!.ReadAsStringAsync(cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://mqttprobe.test/signin-oidc?code=abc&state=xyz") }
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private const string ProviderLoginHtml = """
            <!DOCTYPE html>
            <html><body>
            <form method="post" action="/search">
                <input type="text" name="q" value="decoy" />
                <button type="submit">Search</button>
            </form>
            <form method="post" action="/connect/authorize?scope=openid&amp;state=mystate%2F1">
                <input type="text" name="username" value="placeholder_user" />
                <input type="password" name="password" value="placeholder_pass" />
                <input type="hidden" name="session_code" value="sc-123" />
                <input type="hidden" name="nonce" value="n-456" />
                <input type="hidden" name="consent" value="yes" disabled />
                <button type="submit">Login</button>
            </form>
            </body></html>
            """;
    }

    [Test]
    public async Task SendAppCallbackAsync_GetQueryCallback_SendsGetAndReturnsDisposableResponse()
    {
        var handler = new AppCallbackHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient);

        var callback = new OidcCallbackResult(
            new Uri("https://mqttprobe.test/signin-oidc?code=abc&state=xyz"),
            OidcCallbackMethod.GetQuery,
            Array.Empty<KeyValuePair<string, string>>());
        using var response = await driver.SendAppCallbackAsync(callback);

        handler.RequestCount.Should().Be(1);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.AbsolutePath.Should().Be("/signin-oidc");
        handler.LastRequestUri.Query.Should().Be("?code=abc&state=xyz");
        handler.LastRequestMethod.Should().Be(HttpMethod.Get);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task SendAppCallbackAsync_PostFormCallback_SendsPostWithFormFields()
    {
        var handler = new AppCallbackHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient);

        var formFields = new List<KeyValuePair<string, string>>
        {
            new("code", "auth-code-123"),
            new("state", "state-456"),
            new("iss", "https://idp.test/realms/test"),
            new("session_state", "session-789")
        };
        var callback = new OidcCallbackResult(
            new Uri("https://mqttprobe.test/signin-oidc"),
            OidcCallbackMethod.PostForm,
            formFields);
        using var response = await driver.SendAppCallbackAsync(callback);

        handler.RequestCount.Should().Be(1);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.AbsolutePath.Should().Be("/signin-oidc");
        handler.LastRequestUri.Query.Should().BeEmpty();
        handler.LastRequestMethod.Should().Be(HttpMethod.Post);
        handler.LastPostBody.Should().NotBeNull();
        var body = handler.LastPostBody!;
        body.Should().Contain("code=auth-code-123");
        body.Should().Contain("state=state-456");
        body.Should().Contain("iss=https%3A%2F%2Fidp.test%2Frealms%2Ftest");
        body.Should().Contain("session_state=session-789");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestCase("https://evil.test/signin-oidc?code=abc&state=xyz", "hostile host")]
    [TestCase("http://mqttprobe.test/signin-oidc?code=abc&state=xyz", "HTTP scheme")] // DevSkim: ignore DS137138 HTTP callback scheme is the rejected case under test
    [TestCase("https://mqttprobe.test:8443/signin-oidc?code=abc&state=xyz", "wrong port")]
    [TestCase("https://user@mqttprobe.test/signin-oidc?code=abc&state=xyz", "userinfo")]
    [TestCase("https://mqttprobe.test/not-signin-oidc?code=abc&state=xyz", "wrong path")]
    public async Task SendAppCallbackAsync_HostileCallback_ThrowsAndNeverCallsHandler(
        string callbackUri, string _)
    {
        var handler = new AppCallbackHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient);

        var callback = new OidcCallbackResult(
            new Uri(callbackUri),
            OidcCallbackMethod.GetQuery,
            Array.Empty<KeyValuePair<string, string>>());
        var act = () => driver.SendAppCallbackAsync(callback);
        await act.Should().ThrowAsync<InvalidOperationException>();

        handler.RequestCount.Should().Be(0);
    }

    private sealed class AppCallbackHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }
        internal Uri? LastRequestUri { get; private set; }
        internal HttpMethod? LastRequestMethod { get; private set; }
        internal string? LastPostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            LastRequestMethod = request.Method;

            if (request.Method == HttpMethod.Post && request.Content is not null)
                LastPostBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task BeginLogoutAsync_PostsLogoutFormAndReturnsProviderLogoutUri()
    {
        using var handler = new LogoutRecordingHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient);

        var result = await driver.BeginLogoutAsync();

        handler.GetRequestUri.Should().NotBeNull();
        handler.GetRequestUri!.AbsolutePath.Should().Be("/Logout");

        handler.PostRequestUri.Should().NotBeNull();
        handler.PostRequestUri!.AbsolutePath.Should().Be("/Logout");
        handler.PostRequestUri.Query.Should().Contain("handler=SignOut");
        handler.PostRequestUri.Query.Should().Contain("nonce=x%2F1");

        handler.PostBody.Should().NotBeNull();
        var form = HttpUtility.ParseQueryString(handler.PostBody!);
        form.Get("__RequestVerificationToken").Should().Be("af-token-999");
        form.Get("logoutId").Should().Be("oid-42");

        result.Should().Be(new Uri(
            "https://idp.test/realms/test/protocol/openid-connect/logout?id_token_hint=token&post_logout_redirect_uri=https%3A%2F%2Fmqttprobe.test%2Fsignout-callback-oidc"));
    }

    [Test]
    public async Task BeginLogoutAsync_FormWithoutExplicitAction_SubmitsToCurrentPage()
    {
        using var handler = new NoActionLogoutHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient);

        var result = await driver.BeginLogoutAsync();

        handler.PostRequestUri.Should().NotBeNull();
        handler.PostRequestUri!.AbsolutePath.Should().Be("/Logout");
        handler.PostRequestUri.Query.Should().BeEmpty();

        handler.PostBody.Should().NotBeNull();
        var form = HttpUtility.ParseQueryString(handler.PostBody!);
        form.Get("__RequestVerificationToken").Should().Be("token-no-action");

        result.Should().Be(new Uri("https://mqttprobe.test/Login"));
    }

    [Test]
    public async Task ProviderNetworkFailure_AfterLogoutRedirect_DoesNotRestoreLocalSession()
    {
        using var handler = new LogoutRecordingHandler();
        using var appClient = new HttpClient(handler) { BaseAddress = new Uri("https://mqttprobe.test") };
        using var providerClient = new HttpClient(new ThrowingProviderHandler());
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var logoutUri = await driver.BeginLogoutAsync();

        handler.LocalLogoutCompleted.Should().BeTrue();

        using var homeResponse = await appClient.GetAsync("/");
        homeResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        homeResponse.Headers.Location.Should().Be(new Uri("https://mqttprobe.test/Login"));

        var act = () => driver.ProviderClient.GetAsync(logoutUri);
        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("Provider unavailable");
    }

    private sealed class ThrowingProviderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Provider unavailable");
        }
    }

    private sealed class LogoutRecordingHandler : HttpMessageHandler
    {
        internal Uri? GetRequestUri { get; private set; }
        internal Uri? PostRequestUri { get; private set; }
        internal string? PostBody { get; private set; }
        internal bool LocalLogoutCompleted { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath == "/Logout" &&
                !LocalLogoutCompleted)
            {
                GetRequestUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LogoutPageHtml, System.Text.Encoding.UTF8, "text/html")
                };
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath == "/Logout")
            {
                PostRequestUri = request.RequestUri;
                PostBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                LocalLogoutCompleted = true;

                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers =
                    {
                        Location = new Uri(
                            "https://idp.test/realms/test/protocol/openid-connect/logout?id_token_hint=token&post_logout_redirect_uri=https%3A%2F%2Fmqttprobe.test%2Fsignout-callback-oidc")
                    }
                };
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath == "/" &&
                LocalLogoutCompleted)
            {
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://mqttprobe.test/Login") }
                };
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}");
        }

        private const string LogoutPageHtml = """
            <!DOCTYPE html>
            <html><body>
            <form method="post" action="/SomeOtherPage">
                <input type="hidden" name="__RequestVerificationToken" value="decoy-token" />
                <button type="submit">Decoy</button>
            </form>
            <form method="post" action="/Logout?handler=SignOut&amp;nonce=x%2F1">
                <input name="__RequestVerificationToken" type="hidden" value="af-token-999" />
                <input type="hidden" name="logoutId" value="oid-42" />
                <button type="submit">Logout</button>
            </form>
            </body></html>
            """;
    }

    private sealed class NoActionLogoutHandler : HttpMessageHandler
    {
        internal Uri? PostRequestUri { get; private set; }
        internal string? PostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/Logout")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(NoActionLogoutHtml, System.Text.Encoding.UTF8, "text/html")
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/Logout")
            {
                PostRequestUri = request.RequestUri;
                PostBody = await request.Content!.ReadAsStringAsync(cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://mqttprobe.test/Login") }
                };
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}");
        }

        private const string NoActionLogoutHtml = """
            <!DOCTYPE html>
            <html><body>
            <form method="post">
                <input name="__RequestVerificationToken" type="hidden" value="token-no-action" />
                <button type="submit">Log out</button>
            </form>
            </body></html>
            """;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private bool _getDone;

        internal Uri? GetRequestUri { get; private set; }
        internal Uri? PostRequestUri { get; private set; }
        internal string? PostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_getDone)
            {
                _getDone = true;
                GetRequestUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoginPageHtml, System.Text.Encoding.UTF8, "text/html")
                };
            }

            PostRequestUri = request.RequestUri;
            PostBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers =
                {
                    Location = new Uri("https://idp.test/realms/test/protocol/openid-connect/auth?state=abc")
                }
            };

            response.Headers.Add("Set-Cookie", ".AspNetCore.Correlation.oidc.test=correlationvalue; path=/; secure; samesite=none; httponly");
            response.Headers.Add("Set-Cookie", ".AspNetCore.OpenIdConnect.Nonce.test.noncevalue=noncecookie; path=/; secure; samesite=none; httponly");

            return response;
        }

        private const string LoginPageHtml = """
            <!DOCTYPE html>
            <html><body>
            <form method="post" action="/Login">
                <input type="text" name="username" />
                <input type="hidden" name="returnUrl" value="/" />
                <input name="__RequestVerificationToken" type="hidden" value="decoy-token" />
                <button type="submit">Sign in</button>
            </form>
            <form method="post" action="/Login?handler=Challenge&amp;opaque=a%2Bb">
                <input type="hidden" name="returnUrl" value="/target" />
                <input name="__RequestVerificationToken" type="hidden" value="token-123" />
                <input type="hidden" name="extra" value="value" />
                <input type="hidden" name="disabledField" value="nope" disabled />
                <button type="submit">Sign in with Provider</button>
            </form>
            </body></html>
            """;
    }

    [Test]
    public async Task SubmitProviderConfirmationAsync_DirectRedirect_Returns3xxResponse()
    {
        var handler = new ConfirmationRedirectHandler();
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var logoutUri = new Uri("https://idp.test/realms/test/protocol/openid-connect/logout?client_id=mqttprobe");
        using var result = await driver.SubmitProviderConfirmationAsync(logoutUri);

        handler.GetRequestUri.Should().Be(logoutUri);
        result.StatusCode.Should().Be(HttpStatusCode.Found);
        result.Headers.Location.Should().NotBeNull();
        result.Headers.Location!.Host.Should().Be("mqttprobe.test");
        result.Headers.Location.AbsolutePath.Should().Be("/signout-callback-oidc");
    }

    [Test]
    public async Task SubmitProviderConfirmationAsync_ConfirmationPage_SubmitsFormAndReturnsResponse()
    {
        var handler = new ConfirmationPageHandler();
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var logoutUri = new Uri("https://idp.test/realms/test/protocol/openid-connect/logout?client_id=mqttprobe");
        using var result = await driver.SubmitProviderConfirmationAsync(logoutUri);

        handler.GetRequestUri.Should().Be(logoutUri);
        handler.PostRequestUri.Should().NotBeNull();
        handler.PostRequestUri!.AbsolutePath.Should().Be("/realms/test/protocol/openid-connect/logout-confirm");

        handler.PostBody.Should().NotBeNull();
        var form = HttpUtility.ParseQueryString(handler.PostBody!);
        form.Get("confirm").Should().Be("yes");

        result.StatusCode.Should().Be(HttpStatusCode.Found);
        result.Headers.Location.Should().NotBeNull();
        result.Headers.Location!.Host.Should().Be("mqttprobe.test");
    }

    [Test]
    public async Task SubmitProviderConfirmationAsync_ServerError_ThrowsInvalidOperationException()
    {
        var handler = new ConfirmationErrorHandler(HttpStatusCode.InternalServerError);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var logoutUri = new Uri("https://idp.test/realms/test/protocol/openid-connect/logout?client_id=mqttprobe");
        var act = () => driver.SubmitProviderConfirmationAsync(logoutUri);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*500*");
    }

    [Test]
    public async Task SubmitProviderConfirmationAsync_ClientError_ThrowsInvalidOperationException()
    {
        var handler = new ConfirmationErrorHandler(HttpStatusCode.Forbidden);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var logoutUri = new Uri("https://idp.test/realms/test/protocol/openid-connect/logout?client_id=mqttprobe");
        var act = () => driver.SubmitProviderConfirmationAsync(logoutUri);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*403*");
    }

    private sealed class ConfirmationRedirectHandler : HttpMessageHandler
    {
        internal Uri? GetRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            GetRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://mqttprobe.test/signout-callback-oidc") }
            });
        }
    }

    private sealed class ConfirmationPageHandler : HttpMessageHandler
    {
        internal Uri? GetRequestUri { get; private set; }
        internal Uri? PostRequestUri { get; private set; }
        internal string? PostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetRequestUri = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ConfirmationPageHtml, System.Text.Encoding.UTF8, "text/html")
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                PostRequestUri = request.RequestUri;
                PostBody = await request.Content!.ReadAsStringAsync(cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://mqttprobe.test/signout-callback-oidc") }
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        private const string ConfirmationPageHtml = """
            <!DOCTYPE html>
            <html><body>
            <h1>Confirm Logout</h1>
            <form method="post" action="/realms/test/protocol/openid-connect/logout-confirm">
                <input type="hidden" name="confirm" value="yes" />
                <button type="submit">Confirm</button>
            </form>
            </body></html>
            """;
    }

    private sealed class ConfirmationErrorHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        internal ConfirmationErrorHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }

    [Test]
    public async Task LoopbackCookieHandler_ReplaysSecureCookiesOverHttp()
    {
        var handler = new CookieReplayHandler();
        using var providerClient = new HttpClient(
            new OidcProtocolDriver.LoopbackCookieHandler(handler));
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var getUri = new Uri("http://idp.test/realms/test/protocol/openid-connect/auth"); // DevSkim: ignore DS137138 Secure-cookie replay is proven over http
        using var getResponse = await providerClient.GetAsync(getUri);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var postResponse = await providerClient.PostAsync(
            new Uri("http://idp.test/login-actions/authenticate"), // DevSkim: ignore DS137138 Secure-cookie replay is proven over http
            new StringContent("username=alice&password=pass"));
        postResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        handler.PostCookieHeader.Should().NotBeNullOrWhiteSpace();
        handler.PostCookieHeader.Should().Contain("KC_AUTH_SESSION_ID=abc123");
        handler.PostCookieHeader.Should().Contain("AUTH_SESSION_ID=xyz789");
    }

    [TestCase("AUTH_SESSION_ID=token; path=/; Secure; HttpOnly")]
    [TestCase("AUTH_SESSION_ID=token; path=/; secure; httponly")]
    [TestCase("AUTH_SESSION_ID=token; path=/;SECURE;HttpOnly")]
    [TestCase("AUTH_SESSION_ID=token; Path=/; Secure; HttpOnly; SameSite=None")]
    public async Task LoopbackCookieHandler_HandlesVariousSecureCasings(string setCookieValue)
    {
        var handler = new SingleCookieHandler(setCookieValue);
        using var providerClient = new HttpClient(
            new OidcProtocolDriver.LoopbackCookieHandler(handler));

        using var getResponse = await providerClient.GetAsync(new Uri("https://idp.test/login"));
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var postResponse = await providerClient.PostAsync(
            new Uri("https://idp.test/authenticate"), new StringContent(""));
        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        handler.ReceivedCookieHeader.Should().Contain("AUTH_SESSION_ID=token");
    }

    private sealed class CookieReplayHandler : HttpMessageHandler
    {
        internal string? PostCookieHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("Set-Cookie",
                    "KC_AUTH_SESSION_ID=abc123; path=/; Secure; HttpOnly; SameSite=None");
                response.Headers.Add("Set-Cookie",
                    "AUTH_SESSION_ID=xyz789; path=/; Secure; HttpOnly; SameSite=None");
                return Task.FromResult(response);
            }

            if (request.Method == HttpMethod.Post)
            {
                PostCookieHeader = request.Headers.Contains("Cookie")
                    ? string.Join("; ", request.Headers.GetValues("Cookie"))
                    : null;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://app.test/callback?code=x") }
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class SingleCookieHandler : HttpMessageHandler
    {
        private readonly string _setCookie;
        internal string? ReceivedCookieHeader { get; private set; }

        internal SingleCookieHandler(string setCookie) => _setCookie = setCookie;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("Set-Cookie", _setCookie);
                return Task.FromResult(response);
            }

            ReceivedCookieHeader = request.Headers.Contains("Cookie")
                ? string.Join("; ", request.Headers.GetValues("Cookie"))
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Test]
    public async Task SubmitProviderLoginAsync_FormPost_ParsesKeycloakResponseAndReturnsPostCallback()
    {
        var handler = new FormPostProviderHandler(FormPostProviderHandler.KeycloakFormPostHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var result = await driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");

        result.Method.Should().Be(OidcCallbackMethod.PostForm);
        result.TargetUri.Should().Be(new Uri("https://mqttprobe.test/signin-oidc"));

        result.FormFields.Should().Contain(f => f.Key == "code" && f.Value == "auth-code-value");
        result.FormFields.Should().Contain(f => f.Key == "state" && f.Value == "state-value");
        result.FormFields.Should().Contain(f => f.Key == "iss" && f.Value == "https://idp.test/realms/test");
        result.FormFields.Should().Contain(f => f.Key == "session_state" && f.Value == "session-value");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_FormPost_ExcludesSubmitControl()
    {
        var handler = new FormPostProviderHandler(FormPostProviderHandler.KeycloakFormPostHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var result = await driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");

        result.FormFields.Should().NotContain(f => f.Key == "continue",
            "submit input must be excluded from form fields");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_FormPost_HostileAction_RejectsBeforeContactingApp()
    {
        var hostileHtml = """
            <!DOCTYPE html>
            <html><head><title>OIDC Form_Post Response</title></head><body>
            <form method="POST" action="https://evil.test/signin-oidc">
                <input type="hidden" name="code" value="abc" />
                <input type="hidden" name="state" value="xyz" />
                <input type="submit" name="continue" value="Continue" />
            </form>
            </body></html>
            """;
        var handler = new FormPostProviderHandler(hostileHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var act = () => driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unexpected origin*");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_FormPost_WrongPath_RejectsBeforeContactingApp()
    {
        var wrongPathHtml = """
            <!DOCTYPE html>
            <html><head><title>OIDC Form_Post Response</title></head><body>
            <form method="POST" action="https://mqttprobe.test/other-callback">
                <input type="hidden" name="code" value="abc" />
                <input type="hidden" name="state" value="xyz" />
                <input type="submit" name="continue" value="Continue" />
            </form>
            </body></html>
            """;
        var handler = new FormPostProviderHandler(wrongPathHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var act = () => driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unexpected path*");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_ArbitraryHtml_RejectsWithSafeMessage()
    {
        var arbitraryHtml = """
            <!DOCTYPE html>
            <html><head><title>Login Error</title></head><body>
            <p>Invalid username or password.</p>
            </body></html>
            """;
        var handler = new FormPostProviderHandler(arbitraryHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var act = () => driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unexpected page*");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_MultipleForms_RejectsWithSafeMessage()
    {
        var multiFormHtml = """
            <!DOCTYPE html>
            <html><head><title>OIDC Form_Post Response</title></head><body>
            <form method="POST" action="https://mqttprobe.test/signin-oidc">
                <input type="hidden" name="code" value="abc" />
                <input type="hidden" name="state" value="xyz" />
            </form>
            <form method="POST" action="https://mqttprobe.test/signin-oidc">
                <input type="hidden" name="code" value="def" />
                <input type="hidden" name="state" value="uvw" />
            </form>
            </body></html>
            """;
        var handler = new FormPostProviderHandler(multiFormHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var act = () => driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unexpected page*");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_MissingCodeField_RejectsWithSafeMessage()
    {
        var missingCodeHtml = """
            <!DOCTYPE html>
            <html><head><title>OIDC Form_Post Response</title></head><body>
            <form method="POST" action="https://mqttprobe.test/signin-oidc">
                <input type="hidden" name="state" value="xyz" />
                <input type="submit" name="continue" value="Continue" />
            </form>
            </body></html>
            """;
        var handler = new FormPostProviderHandler(missingCodeHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var act = () => driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing required code or state*");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_MissingStateField_RejectsWithSafeMessage()
    {
        var missingStateHtml = """
            <!DOCTYPE html>
            <html><head><title>OIDC Form_Post Response</title></head><body>
            <form method="POST" action="https://mqttprobe.test/signin-oidc">
                <input type="hidden" name="code" value="abc" />
                <input type="submit" name="continue" value="Continue" />
            </form>
            </body></html>
            """;
        var handler = new FormPostProviderHandler(missingStateHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var act = () => driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing required code or state*");
    }

    [Test]
    public async Task SubmitProviderLoginAsync_EmptyCodeField_RejectsWithSafeMessage()
    {
        var emptyCodeHtml = """
            <!DOCTYPE html>
            <html><head><title>OIDC Form_Post Response</title></head><body>
            <form method="POST" action="https://mqttprobe.test/signin-oidc">
                <input type="hidden" name="code" value="" />
                <input type="hidden" name="state" value="xyz" />
                <input type="submit" name="continue" value="Continue" />
            </form>
            </body></html>
            """;
        var handler = new FormPostProviderHandler(emptyCodeHtml);
        using var providerClient = new HttpClient(handler);
        using var appClient = new HttpClient { BaseAddress = new Uri("https://mqttprobe.test") };
        using var driver = new OidcProtocolDriver(appClient, providerClient);

        var authUri = new Uri("https://idp.test/authorize?client_id=mqttprobe");
        var act = () => driver.SubmitProviderLoginAsync(authUri, "alice", "s3cret");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing required code or state*");
    }

    private sealed class FormPostProviderHandler : HttpMessageHandler
    {
        private readonly string _html;

        internal FormPostProviderHandler(string html) => _html = html;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ProviderLoginHtml, System.Text.Encoding.UTF8, "text/html")
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_html, System.Text.Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private const string ProviderLoginHtml = """
            <!DOCTYPE html>
            <html><body>
            <form method="post" action="/connect/authorize">
                <input type="text" name="username" value="placeholder_user" />
                <input type="password" name="password" value="placeholder_pass" />
                <button type="submit">Login</button>
            </form>
            </body></html>
            """;

        internal const string KeycloakFormPostHtml = """
            <!DOCTYPE html>
            <html><head><title>OIDC Form_Post Response</title></head><body>
            <form method="POST" action="https://mqttprobe.test/signin-oidc">
                <input type="hidden" name="code" value="auth-code-value" />
                <input type="hidden" name="iss" value="https://idp.test/realms/test" />
                <input type="hidden" name="state" value="state-value" />
                <input type="hidden" name="session_state" value="session-value" />
                <input type="submit" name="continue" value="Continue" />
            </form>
            </body></html>
            """;
    }
}
