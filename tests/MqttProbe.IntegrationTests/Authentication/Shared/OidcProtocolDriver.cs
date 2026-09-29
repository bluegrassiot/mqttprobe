using System.Net;
using AngleSharp.Html.Parser;

namespace MqttProbe.IntegrationTests.Authentication;

internal enum OidcCallbackMethod
{
    GetQuery,
    PostForm
}

internal sealed class OidcCallbackResult
{
    internal OidcCallbackResult(
        Uri targetUri,
        OidcCallbackMethod method,
        IReadOnlyList<KeyValuePair<string, string>> formFields)
    {
        TargetUri = targetUri;
        Method = method;
        FormFields = formFields;
    }

    internal Uri TargetUri { get; }
    internal OidcCallbackMethod Method { get; }
    internal IReadOnlyList<KeyValuePair<string, string>> FormFields { get; }
}

internal sealed class OidcProtocolDriver : IDisposable
{
    private readonly HttpClient _providerClient;
    private readonly CookieContainer _providerCookies;
    private readonly bool _ownsProviderClient;
    private bool _disposed;

    public OidcProtocolDriver(HttpClient appClient)
    {
        ArgumentNullException.ThrowIfNull(appClient);

        AppClient = appClient;

        _providerCookies = new CookieContainer();
        var innerHandler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        };

        _providerClient = new HttpClient(new LoopbackCookieHandler(innerHandler));
        _ownsProviderClient = true;
    }

    internal OidcProtocolDriver(
        HttpClient appClient,
        HttpClient providerClient,
        CookieContainer? providerCookies = null)
    {
        ArgumentNullException.ThrowIfNull(appClient);
        ArgumentNullException.ThrowIfNull(providerClient);

        AppClient = appClient;
        _providerClient = providerClient;
        _providerCookies = providerCookies ?? new CookieContainer();
        _ownsProviderClient = false;
    }

    internal HttpClient AppClient { get; }

    internal HttpClient ProviderClient => _providerClient;

    internal CookieContainer ProviderCookies => _providerCookies;

    internal IReadOnlyList<string> ChallengeSetCookieHeaders { get; private set; } = Array.Empty<string>();

    internal async Task<OidcCallbackResult> SubmitProviderLoginAsync(
        Uri authorizationUri,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        if (!authorizationUri.IsAbsoluteUri)
            throw new ArgumentException("Authorization URI must be absolute.", nameof(authorizationUri));

        using var getResponse = await ProviderClient.GetAsync(authorizationUri, cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var html = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        var document = new HtmlParser().ParseDocument(html);

        var forms = document.QuerySelectorAll("form");
        var form = FindCredentialForm(forms);

        var formData = CollectEnabledInputs(form);

        for (var i = 0; i < formData.Count; i++)
        {
            if (formData[i].Key == "username")
                formData[i] = new KeyValuePair<string, string>("username", username);
            else if (formData[i].Key == "password")
                formData[i] = new KeyValuePair<string, string>("password", password);
        }

        var action = ResolveAction(form, authorizationUri);

        using var postContent = new FormUrlEncodedContent(formData);
        using var postResponse = await ProviderClient.PostAsync(action, postContent, cancellationToken);

        if ((int)postResponse.StatusCode >= 300 && (int)postResponse.StatusCode < 400)
        {
            var location = postResponse.Headers.Location
                ?? throw new InvalidOperationException("Provider login redirect response is missing Location header.");

            var callbackUri = location.IsAbsoluteUri ? location : new Uri(action, location);
            return new OidcCallbackResult(
                callbackUri,
                OidcCallbackMethod.GetQuery,
                Array.Empty<KeyValuePair<string, string>>());
        }

        if (postResponse.StatusCode == HttpStatusCode.OK)
            return await ParseFormPostResponseAsync(postResponse, action, cancellationToken);

        throw new InvalidOperationException(
            $"Expected 3xx redirect or 200 form_post from provider login POST but got {(int)postResponse.StatusCode}.");
    }

    private async Task<OidcCallbackResult> ParseFormPostResponseAsync(
        HttpResponseMessage postResponse,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        var baseAddress = AppClient.BaseAddress
            ?? throw new InvalidOperationException("AppClient.BaseAddress must be set.");

        var responseHtml = await postResponse.Content.ReadAsStringAsync(cancellationToken);
        var document = new HtmlParser().ParseDocument(responseHtml);

        var pageForms = document.QuerySelectorAll("form");
        if (pageForms.Length != 1)
            throw new InvalidOperationException(
                "Provider returned an unexpected page (expected exactly one form_post form).");

        var formPostForm = pageForms[0];
        var formAction = ResolveAction(formPostForm, requestUri);

        ValidateFormPostAction(formAction, baseAddress);

        var formFields = CollectFormPostFields(formPostForm);

        var hasCode = formFields.Any(f => f.Key == "code" && !string.IsNullOrEmpty(f.Value));
        var hasState = formFields.Any(f => f.Key == "state" && !string.IsNullOrEmpty(f.Value));
        if (!hasCode || !hasState)
            throw new InvalidOperationException(
                "Provider form_post response is missing required code or state fields.");

        return new OidcCallbackResult(formAction, OidcCallbackMethod.PostForm, formFields);
    }

    private static void ValidateFormPostAction(Uri formAction, Uri baseAddress)
    {
        if (!string.Equals(formAction.Scheme, baseAddress.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(formAction.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            formAction.Port != baseAddress.Port)
            throw new InvalidOperationException(
                "Provider form_post action targets an unexpected origin.");

        if (!string.Equals(formAction.AbsolutePath, "/signin-oidc", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Provider form_post action targets an unexpected path.");
    }

    private static List<KeyValuePair<string, string>> CollectFormPostFields(AngleSharp.Dom.IElement form)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        foreach (var input in form.QuerySelectorAll("input"))
        {
            if (input.HasAttribute("disabled"))
                continue;

            var inputType = (input.GetAttribute("type") ?? "").ToLowerInvariant();
            if (inputType is "submit" or "button")
                continue;

            var name = input.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;

            pairs.Add(new KeyValuePair<string, string>(name, input.GetAttribute("value") ?? ""));
        }

        return pairs;
    }

    internal async Task<Uri> BeginLogoutAsync(CancellationToken cancellationToken = default)
    {
        var baseAddress = AppClient.BaseAddress
            ?? throw new InvalidOperationException("AppClient.BaseAddress must be set.");

        var requestUri = new Uri(baseAddress, "/Logout");

        using var getResponse = await AppClient.GetAsync("/Logout", cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var html = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        var document = new HtmlParser().ParseDocument(html);

        var forms = document.QuerySelectorAll("form");
        var logoutForm = FindLogoutForm(forms, requestUri);

        var formData = CollectEnabledInputs(logoutForm);
        var action = ResolveAction(logoutForm, requestUri);

        using var postContent = new FormUrlEncodedContent(formData);
        using var postResponse = await AppClient.PostAsync(action, postContent, cancellationToken);

        if ((int)postResponse.StatusCode < 300 || (int)postResponse.StatusCode >= 400)
            throw new InvalidOperationException(
                $"Expected 3xx redirect from logout POST but got {(int)postResponse.StatusCode}.");

        var location = postResponse.Headers.Location
            ?? throw new InvalidOperationException("Logout redirect response is missing Location header.");

        return location.IsAbsoluteUri ? location : new Uri(action, location);
    }

    internal async Task<HttpResponseMessage> SubmitProviderConfirmationAsync(
        Uri pageUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageUri);

        if (!pageUri.IsAbsoluteUri)
            throw new ArgumentException("Page URI must be absolute.", nameof(pageUri));

        var getResponse = await ProviderClient.GetAsync(pageUri, cancellationToken);

        // 3xx direct redirect (no confirmation page) is a valid result.
        if ((int)getResponse.StatusCode >= 300 && (int)getResponse.StatusCode < 400)
            return getResponse;

        if (!getResponse.IsSuccessStatusCode)
        {
            getResponse.Dispose();
            throw new InvalidOperationException(
                $"Provider confirmation page returned {(int)getResponse.StatusCode}.");
        }

        var html = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        getResponse.Dispose();
        var document = new HtmlParser().ParseDocument(html);

        var forms = document.QuerySelectorAll("form");
        if (forms.Length == 0)
            throw new InvalidOperationException("No forms found on the provider confirmation page.");

        // Find the first form with a submit button (confirmation form)
        AngleSharp.Dom.IElement? confirmForm = null;
        foreach (var form in forms)
        {
            if (form.QuerySelector("input[type='submit'], button[type='submit']") is not null)
            {
                confirmForm = form;
                break;
            }
        }

        confirmForm ??= forms[0];

        var formData = CollectEnabledInputs(confirmForm);
        var action = ResolveAction(confirmForm, pageUri);

        var postContent = new FormUrlEncodedContent(formData);
        return await ProviderClient.PostAsync(action, postContent, cancellationToken);
    }

    internal async Task<Uri> BeginLoginAsync(string returnUrl = "/", CancellationToken cancellationToken = default)
    {
        var baseAddress = AppClient.BaseAddress
            ?? throw new InvalidOperationException("AppClient.BaseAddress must be set.");

        var relativePath = $"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";
        var requestUri = new Uri(baseAddress, relativePath);

        using var getResponse = await AppClient.GetAsync(relativePath, cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var html = await getResponse.Content.ReadAsStringAsync(cancellationToken);
        var document = new HtmlParser().ParseDocument(html);

        var forms = document.QuerySelectorAll("form");
        var challengeForm = FindChallengeForm(forms, requestUri);

        var formData = CollectEnabledInputs(challengeForm);
        var action = ResolveAction(challengeForm, requestUri);

        using var postContent = new FormUrlEncodedContent(formData);
        using var postResponse = await AppClient.PostAsync(action, postContent, cancellationToken);

        // Snapshot Set-Cookie headers before the response is disposed.
        ChallengeSetCookieHeaders = postResponse.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.ToList().AsReadOnly()
            : Array.Empty<string>();

        if ((int)postResponse.StatusCode < 300 || (int)postResponse.StatusCode >= 400)
            throw new InvalidOperationException(
                $"Expected 3xx redirect from challenge POST but got {(int)postResponse.StatusCode}.");

        var location = postResponse.Headers.Location
            ?? throw new InvalidOperationException("Challenge redirect response is missing Location header.");

        return location.IsAbsoluteUri ? location : new Uri(action, location);
    }

    internal async Task<HttpResponseMessage> SendAppCallbackAsync(
        OidcCallbackResult callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var baseAddress = AppClient.BaseAddress
            ?? throw new InvalidOperationException("AppClient.BaseAddress must be set.");

        if (!string.IsNullOrEmpty(callback.TargetUri.UserInfo))
            throw new InvalidOperationException("Callback target URI must not contain userinfo.");

        if (!string.Equals(callback.TargetUri.Scheme, baseAddress.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(callback.TargetUri.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            callback.TargetUri.Port != baseAddress.Port)
            throw new InvalidOperationException("Callback target URI origin does not match AppClient.BaseAddress.");

        if (!string.Equals(callback.TargetUri.AbsolutePath, "/signin-oidc", StringComparison.Ordinal))
            throw new InvalidOperationException("Callback target URI path must be /signin-oidc.");

        return callback.Method switch
        {
            OidcCallbackMethod.GetQuery =>
                await AppClient.GetAsync(callback.TargetUri.PathAndQuery, cancellationToken),
            OidcCallbackMethod.PostForm =>
                await AppClient.PostAsync(
                    callback.TargetUri,
                    new FormUrlEncodedContent(callback.FormFields),
                    cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported callback method: {callback.Method}.")
        };
    }

    private static AngleSharp.Dom.IElement FindChallengeForm(
        AngleSharp.Dom.IHtmlCollection<AngleSharp.Dom.IElement> forms, Uri requestUri)
    {
        AngleSharp.Dom.IElement? match = null;

        foreach (var form in forms)
        {
            if (form.QuerySelector("input[name='__RequestVerificationToken']") is null)
                continue;

            var rawAction = form.GetAttribute("action");
            if (rawAction is null)
                continue;

            var resolved = new Uri(requestUri, rawAction);
            if (!string.Equals(resolved.AbsolutePath, "/Login", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!resolved.Query.Contains("handler=Challenge", StringComparison.OrdinalIgnoreCase))
                continue;

            if (match is not null)
                throw new InvalidOperationException("Multiple challenge forms found on the login page.");

            match = form;
        }

        return match
            ?? throw new InvalidOperationException("No challenge form found on the login page.");
    }

    private static AngleSharp.Dom.IElement FindLogoutForm(
        AngleSharp.Dom.IHtmlCollection<AngleSharp.Dom.IElement> forms, Uri requestUri)
    {
        AngleSharp.Dom.IElement? match = null;

        foreach (var form in forms)
        {
            if (form.QuerySelector("input[name='__RequestVerificationToken']") is null)
                continue;

            var rawAction = form.GetAttribute("action");
            var resolved = string.IsNullOrEmpty(rawAction)
                ? requestUri
                : new Uri(requestUri, rawAction);

            if (!string.Equals(resolved.AbsolutePath, "/Logout", StringComparison.OrdinalIgnoreCase))
                continue;

            if (match is not null)
                throw new InvalidOperationException("Multiple logout forms found on the logout page.");

            match = form;
        }

        return match
            ?? throw new InvalidOperationException("No logout form found on the logout page.");
    }

    private static AngleSharp.Dom.IElement FindCredentialForm(
        AngleSharp.Dom.IHtmlCollection<AngleSharp.Dom.IElement> forms)
    {
        AngleSharp.Dom.IElement? match = null;

        foreach (var form in forms)
        {
            var hasUsername = form.QuerySelector("input[name='username']:not([disabled])") is not null;
            var hasPassword = form.QuerySelector("input[name='password']:not([disabled])") is not null;

            if (!hasUsername || !hasPassword)
                continue;

            if (match is not null)
                throw new InvalidOperationException(
                    "Multiple forms with username and password inputs found on the provider login page.");

            match = form;
        }

        return match
            ?? throw new InvalidOperationException(
                "No form with username and password inputs found on the provider login page.");
    }

    private static List<KeyValuePair<string, string>> CollectEnabledInputs(AngleSharp.Dom.IElement form)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        foreach (var input in form.QuerySelectorAll("input"))
        {
            if (input.HasAttribute("disabled"))
                continue;

            var name = input.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;

            pairs.Add(new KeyValuePair<string, string>(name, input.GetAttribute("value") ?? ""));
        }

        return pairs;
    }

    private static Uri ResolveAction(AngleSharp.Dom.IElement form, Uri requestUri)
    {
        var rawAction = form.GetAttribute("action");
        return string.IsNullOrEmpty(rawAction)
            ? requestUri
            : new Uri(requestUri, rawAction);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_ownsProviderClient)
            _providerClient.Dispose();

        _disposed = true;
    }

    internal sealed class LoopbackCookieHandler : DelegatingHandler
    {
        private readonly Dictionary<string, string> _jar = new(StringComparer.OrdinalIgnoreCase);

        internal LoopbackCookieHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_jar.Count > 0)
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _jar.Values));

            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var headers))
            {
                foreach (var header in headers)
                    StoreCookie(header);
            }

            return response;
        }

        private void StoreCookie(string setCookieHeader)
        {
            var directives = setCookieHeader.Split(';', StringSplitOptions.TrimEntries);
            if (directives.Length == 0)
                return;

            var nv = directives[0].Split('=', 2);
            if (nv.Length < 2)
                return;

            var name = nv[0];
            var value = nv[1];

            _jar[name] = $"{name}={value}";
        }
    }
}
