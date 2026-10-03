using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MqttProbe.Core.Services.Mqtt;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class BackChannelLogoutHandlerTests
{
    private LogoutTokenFactory _tokens = null!;

    [SetUp]
    public void SetUp() => _tokens = new LogoutTokenFactory();

    [TearDown]
    public void TearDown() => _tokens.Dispose();

    private async Task<EndpointHost> StartHostAsync(TimeSpan? teardownTimeout = null)
        => await EndpointHost.StartAsync(_tokens, teardownTimeout);

    private string CreateLogoutToken(string? sid = "sid-1", string? subject = "user-123", string? tokenId = "jti-1")
        => _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(sid: sid, subject: subject, tokenId: tokenId));

    private static Task<HttpResponseMessage> PostTokenAsync(HttpClient client, string? logoutToken)
    {
        var body = logoutToken is null
            ? "unrelated=value"
            : "logout_token=" + Uri.EscapeDataString(logoutToken);

        return client.PostAsync(
            BackChannelLogoutEndpoint.Path,
            new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Test]
    public async Task ValidLogoutToken_RevokesOnlyTheSessionItNames()
    {
        await using var host = await StartHostAsync();
        var target = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var sibling = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-2");
        var unrelated = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-456", "Other"), sid: "sid-1");

        // No cookie and no antiforgery token: this traffic is server-to-server.
        var response = await PostTokenAsync(host.Client, CreateLogoutToken());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Coordinator.GetSession(target.SessionId).Should().BeNull();
        host.Coordinator.GetSession(sibling.SessionId).Should().NotBeNull();
        host.Coordinator.GetSession(unrelated.SessionId).Should().NotBeNull();
    }

    [Test]
    public async Task LogoutTokenWithoutSid_RevokesEverySessionOfThatSubject()
    {
        await using var host = await StartHostAsync();
        var first = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var second = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-2");
        var other = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-456", "Other"), sid: "sid-3");

        var response = await PostTokenAsync(host.Client, CreateLogoutToken(sid: null, tokenId: "jti-nosid"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Coordinator.GetSession(first.SessionId).Should().BeNull();
        host.Coordinator.GetSession(second.SessionId).Should().BeNull();
        host.Coordinator.GetSession(other.SessionId).Should().NotBeNull();
    }

    [Test]
    public async Task LogoutTokenForUnknownSession_ReturnsOk()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");

        var response = await PostTokenAsync(host.Client, CreateLogoutToken(sid: "no-such-sid"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    // ── Security rejections ─────────────────────────────────────────────────

    [Test]
    public async Task LogoutTokenWithForeignSid_DoesNotRevokeThroughSubjectMatch()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");

        var response = await PostTokenAsync(host.Client, CreateLogoutToken(sid: "forged-sid"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    [Test]
    public async Task ReplayedLogoutToken_IsRejected()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var token = CreateLogoutToken();

        var first = await PostTokenAsync(host.Client, token);
        var second = await PostTokenAsync(host.Client, token);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    [Test]
    public async Task TamperedLogoutToken_IsRejectedWithoutRevoking()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var token = CreateLogoutToken();
        var tampered = token[..^2] + (token[^2] == 'A' ? "B" : "A");

        var response = await PostTokenAsync(host.Client, tampered);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Coordinator.GetSession(record.SessionId).Should().NotBeNull();
        (await response.Content.ReadAsStringAsync()).Should().NotContain(tampered);
    }

    [Test]
    public async Task ExpiredLogoutToken_IsRejectedWithoutRevoking()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(
            sid: "sid-1", issuedAt: now - 3600, expiresAt: now - 3000));

        var response = await PostTokenAsync(host.Client, expired);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    [Test]
    public async Task NonceBearingLogoutToken_IsRejectedWithoutRevoking()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var withNonce = _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(sid: "sid-1", nonce: "n-1"));

        var response = await PostTokenAsync(host.Client, withNonce);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Coordinator.GetSession(record.SessionId).Should().NotBeNull();
    }

    // ── Request shape ───────────────────────────────────────────────────────

    [Test]
    public async Task MissingLogoutTokenField_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();

        var response = await PostTokenAsync(host.Client, logoutToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DuplicateLogoutTokenFields_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();
        var token = CreateLogoutToken();
        var body = "logout_token=" + Uri.EscapeDataString(token) +
            "&logout_token=" + Uri.EscapeDataString(CreateLogoutToken(tokenId: "jti-2"));

        var response = await host.Client.PostAsync(
            BackChannelLogoutEndpoint.Path,
            new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task NonFormContentType_ReturnsUnsupportedMediaType()
    {
        await using var host = await StartHostAsync();

        var response = await host.Client.PostAsync(
            BackChannelLogoutEndpoint.Path,
            new StringContent(CreateLogoutToken(), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Test]
    public async Task Get_IsRejected()
    {
        await using var host = await StartHostAsync();

        var response = await host.Client.GetAsync(BackChannelLogoutEndpoint.Path);

        // Endpoint routing answers the method mismatch before the handler runs.
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task OversizedBody_ReturnsPayloadTooLarge()
    {
        await using var host = await StartHostAsync();
        var body = "logout_token=" + new string('A', BackChannelLogoutHandler.MaxRequestBodyBytes + 4096);

        var response = await host.Client.PostAsync(
            BackChannelLogoutEndpoint.Path,
            new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    // ── Claim lifecycle: reserve → complete / release ─────────────────────────

    [Test]
    public async Task ValidTokenWithNoMatchingSession_ConsumesTheJti()
    {
        await using var host = await StartHostAsync();
        var token = CreateLogoutToken(sid: "no-such-sid");

        var first = await PostTokenAsync(host.Client, token);
        var replay = await PostTokenAsync(host.Client, token);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SimultaneousCopiesOfOneToken_ExactlyOneRequestActs()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var token = CreateLogoutToken();

        var responses = await Task.WhenAll(
            PostTokenAsync(host.Client, token),
            PostTokenAsync(host.Client, token));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
        host.Coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    [Test]
    public async Task GateCallbackFailure_StillInvalidatesEveryGateAndConsumesTheJti()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var throwingLogger = Substitute.For<ILogger<RevocableSessionActivityGate>>();
        var throwingGate = new RevocableSessionActivityGate(throwingLogger);
        var siblingGate = new RevocableSessionActivityGate();
        var throwingTeardown = Substitute.For<ICircuitTeardownHandler>();
        var siblingTeardown = Substitute.For<ICircuitTeardownHandler>();

        throwingGate.RevocationToken.Register(() => throw new InvalidOperationException("revocation failed"));
        BindLease(host, record, throwingGate, throwingTeardown, "circuit-1");
        BindLease(host, record, siblingGate, siblingTeardown, "circuit-2");
        var token = CreateLogoutToken();

        var accepted = await PostTokenAsync(host.Client, token);
        var replay = await PostTokenAsync(host.Client, token);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        throwingGate.IsActive.Should().BeFalse();
        siblingGate.IsActive.Should().BeFalse();
        host.Coordinator.GetSession(record.SessionId).Should().BeNull();
        await throwingTeardown.Received(1).TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>());
        await siblingTeardown.Received(1).TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>());

        await throwingGate.RevocationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        WarningMessages(throwingLogger).Should().Contain(message =>
            message.Contains("revocation callback failed"));
        host.LogMessages.Should().NotContain(message => message.Contains(token));
    }

    [Test]
    public async Task TeardownTimeout_AfterInvalidation_StillReturnsOkAndConsumesTheJti()
    {
        await using var host = await StartHostAsync(TimeSpan.FromMilliseconds(400));
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        BindLease(host, record, new RevocableSessionActivityGate(), new StuckTeardownHandler());
        var token = CreateLogoutToken();

        var accepted = await PostTokenAsync(host.Client, token);
        var replay = await PostTokenAsync(host.Client, token);

        // The gate closed before cleanup stalled, so the identity provider must
        // not be invited to send this token again.
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        record.AreGatesRevoked.Should().BeTrue();
        host.Coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    [Test]
    public async Task TeardownCancellationToken_IsNotTheRequestsAbortedToken()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        CancellationToken observed = default;
        BindLease(
            host,
            record,
            new RevocableSessionActivityGate(),
            new ObservingTeardownHandler(token => observed = token));

        var response = await PostTokenAsync(host.Client, CreateLogoutToken());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        observed.CanBeCanceled.Should().BeTrue();
        observed.Should().NotBe(host.LastRequestAborted);
    }

    [Test]
    public async Task ClaimCacheAtCapacity_RejectsFreshTokenAndKeepsHeldClaims()
    {
        await using var host = await StartHostAsync();
        var cache = host.ReplayCache;
        var heldExpiry = DateTimeOffset.UtcNow.AddSeconds(90);

        for (var i = 0; i < BackChannelLogoutReplayCache.MaxEntries; i++)
        {
            cache.TryReserve(LogoutTokenFactory.Issuer, $"filler-{i}", heldExpiry)
                .Should().Be(BackChannelLogoutReservation.Reserved);
        }

        // The fresh token outlives every held claim, so only an evicting cache
        // could admit it.
        var fresh = await PostTokenAsync(host.Client, CreateLogoutToken(tokenId: "jti-fresh"));
        var held = await PostTokenAsync(host.Client, CreateLogoutToken(tokenId: "filler-0"));

        fresh.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        held.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        cache.Count.Should().Be(BackChannelLogoutReplayCache.MaxEntries);
        host.LogMessages.Should().Contain(message => message.Contains("claim_cache_full"));
    }

    // ── Revocation is started for every match before waiting ────────────────

    [Test]
    public async Task BlockedFirstSessionTeardown_StillRevokesTheRemainingSessions()
    {
        await using var host = await StartHostAsync(TimeSpan.FromMilliseconds(400));
        var blocked = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var sibling = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-2");
        var blockedGate = new RevocableSessionActivityGate();
        var siblingGate = new RevocableSessionActivityGate();
        var blockedTeardown = new BlockingTeardownHandler();
        var siblingTeardown = Substitute.For<ICircuitTeardownHandler>();
        BindLease(host, blocked, blockedGate, blockedTeardown, "circuit-1");
        BindLease(host, sibling, siblingGate, siblingTeardown, "circuit-2");

        var response = await PostTokenAsync(
            host.Client, CreateLogoutToken(sid: null, tokenId: "jti-blocked"));

        // A teardown that never returns cannot hold the sibling session open.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        blockedGate.IsActive.Should().BeFalse();
        siblingGate.IsActive.Should().BeFalse();
        blockedTeardown.Entered.Should().BeTrue();
        await siblingTeardown.Received(1).TeardownAsync(Arg.Any<Func<bool>>(), Arg.Any<CancellationToken>());
        host.Coordinator.GetSession(sibling.SessionId).Should().BeNull();
        sibling.AreGatesRevoked.Should().BeTrue();
    }

    // ── A blocked callback can hold neither the response nor a sibling ─────

    [Test]
    public async Task BlockingRevocationCallbacks_BothSessionsCloseBeforeTheResponseIsReleased()
    {
        var release = new ManualResetEventSlim(false);
        RevocableSessionActivityGate? firstGate = null;
        RevocableSessionActivityGate? secondGate = null;

        try
        {
            await using var host = await StartHostAsync(TimeSpan.FromSeconds(5));
            var first = host.Coordinator.CreateSession(
                ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
            var second = host.Coordinator.CreateSession(
                ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-2");
            firstGate = new RevocableSessionActivityGate();
            secondGate = new RevocableSessionActivityGate();

            // The bound only keeps a regression from hanging the run; the test
            // releases both callbacks in its finally.
            firstGate.RevocationToken.Register(() => release.Wait(TimeSpan.FromSeconds(15)));
            secondGate.RevocationToken.Register(() => release.Wait(TimeSpan.FromSeconds(15)));
            BindLease(host, first, firstGate, Substitute.For<ICircuitTeardownHandler>(), "circuit-1");
            BindLease(host, second, secondGate, Substitute.For<ICircuitTeardownHandler>(), "circuit-2");

            var stopwatch = Stopwatch.StartNew();
            var response = await PostTokenAsync(
                host.Client, CreateLogoutToken(sid: null, tokenId: "jti-blocking"));
            stopwatch.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            firstGate.IsActive.Should().BeFalse();
            secondGate.IsActive.Should().BeFalse();
            first.AreGatesRevoked.Should().BeTrue();
            second.AreGatesRevoked.Should().BeTrue();
            host.Coordinator.GetSession(first.SessionId).Should().BeNull();
            host.Coordinator.GetSession(second.SessionId).Should().BeNull();
        }
        finally
        {
            release.Set();
        }

        if (firstGate is not null && secondGate is not null)
        {
            await Task.WhenAll(firstGate.RevocationCompletion, secondGate.RevocationCompletion)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        release.Dispose();
    }

    // ── A retry cannot claim a revocation it never performed ────────────────

    [Test]
    public async Task SecondTokenForARevokedSession_CannotClaimAnotherRevocation()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        BindLease(host, record, new RevocableSessionActivityGate(), Substitute.For<ICircuitTeardownHandler>());

        var first = await PostTokenAsync(host.Client, CreateLogoutToken(tokenId: "jti-first"));
        var retry = await PostTokenAsync(host.Client, CreateLogoutToken(tokenId: "jti-retry"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);

        // The record is gone, so the retry revokes nothing and must say so
        // rather than reporting a second revocation it never performed.
        host.Coordinator.GetSession(record.SessionId).Should().BeNull();
        record.AreGatesRevoked.Should().BeTrue();
        host.LogMessages.Should().Contain(message => message.Contains("1 session(s) revoked"));
        host.LogMessages.Should().Contain(message => message.Contains("0 session(s) revoked"));
    }

    // ── Invalidation decides the response, cleanup does not ─────────────────

    [Test]
    public async Task LateTeardownFault_IsLoggedAfterTheResponseIsSent()
    {
        await using var host = await StartHostAsync(TimeSpan.FromMilliseconds(300));
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        BindLease(host, record, new RevocableSessionActivityGate(), new LateFaultTeardownHandler());
        var token = CreateLogoutToken(tokenId: "jti-late");

        var stopwatch = Stopwatch.StartNew();
        var accepted = await PostTokenAsync(host.Client, token);
        stopwatch.Stop();
        var replay = await PostTokenAsync(host.Client, token);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        record.AreGatesRevoked.Should().BeTrue();

        // The teardown fails once the response is already gone; the failure is
        // still observed and logged instead of vanishing with the abandoned wait,
        // and the record only leaves the registry after that cleanup ends.
        await WaitUntilAsync(
            () => host.Coordinator.GetSession(record.SessionId) is null,
            TimeSpan.FromSeconds(10));
        host.LogMessages.Should().Contain(message => message.Contains("Circuit teardown failed"));
    }

    // ── Cache-Control ───────────────────────────────────────────────────────

    [Test]
    public async Task EveryResponse_CarriesNoStore()
    {
        await using var host = await StartHostAsync();
        host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");

        var accepted = await PostTokenAsync(host.Client, CreateLogoutToken());
        var malformed = await PostTokenAsync(host.Client, logoutToken: null);
        var wrongContentType = await host.Client.PostAsync(
            BackChannelLogoutEndpoint.Path,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrongContentType.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);

        var methodNotAllowed = new DefaultHttpContext();
        methodNotAllowed.Request.Method = HttpMethods.Get;
        methodNotAllowed.Response.Body = new MemoryStream();
        await host.Handler.HandleAsync(methodNotAllowed);

        var oversized = CreateFormContext(
            FormBody("logout_token=" + new string('A', BackChannelLogoutHandler.MaxRequestBodyBytes + 1)));
        await host.Handler.HandleAsync(oversized);

        foreach (var response in new[] { accepted, malformed, wrongContentType })
        {
            response.Headers.CacheControl.Should().NotBeNull();
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }

        methodNotAllowed.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        oversized.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    private static void BindLease(
        EndpointHost host,
        AppSessionRecord record,
        RevocableSessionActivityGate gate,
        ICircuitTeardownHandler teardownHandler,
        string circuitId = "circuit-1")
    {
        var lease = new CircuitLease(gate, teardownHandler);
        lease.TryBind(circuitId, record);

        host.Coordinator
            .ValidateAndRegister(record.SessionId, LogoutTokenFactory.Issuer, "user-123", lease)
            .Should().NotBeNull();
    }

    // ── Body bound without the server's size-limit feature ───────────────────

    [Test]
    public async Task OversizedBody_WithoutSizeLimitFeature_ReturnsPayloadTooLarge()
    {
        await using var host = await StartHostAsync();
        var context = CreateFormContext(
            FormBody("logout_token=" + new string('A', BackChannelLogoutHandler.MaxRequestBodyBytes + 1)));

        context.Request.ContentLength.Should().BeNull();
        context.Features.Get<IHttpMaxRequestBodySizeFeature>().Should().BeNull();

        await host.Handler.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Test]
    public async Task OversizedBody_WithReadOnlySizeLimitFeature_ReturnsPayloadTooLarge()
    {
        await using var host = await StartHostAsync();
        var context = CreateFormContext(
            FormBody("logout_token=" + new string('A', BackChannelLogoutHandler.MaxRequestBodyBytes + 1)),
            readOnlySizeFeature: true);

        // The read-only feature rejects writes, so a handler that tried to set
        // the limit would blow up here instead of answering 413.
        await host.Handler.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Test]
    public async Task ValidToken_WithoutSizeLimitFeature_IsAccepted()
    {
        await using var host = await StartHostAsync();
        var record = host.Coordinator.CreateSession(
            ExternalIdentity.Create(LogoutTokenFactory.Issuer, "user-123", "User"), sid: "sid-1");
        var context = CreateFormContext(
            FormBody("logout_token=" + Uri.EscapeDataString(CreateLogoutToken())));

        await host.Handler.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        host.Coordinator.GetSession(record.SessionId).Should().BeNull();
    }

    [Test]
    public async Task ChunkedOversizedBody_ReturnsPayloadTooLarge()
    {
        await using var host = await StartHostAsync();
        var body = "logout_token=" + new string('A', BackChannelLogoutHandler.MaxRequestBodyBytes + 4096);

        using var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var request = new HttpRequestMessage(HttpMethod.Post, BackChannelLogoutEndpoint.Path);
        request.Content = content;
        request.Headers.TransferEncodingChunked = true;

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task ServerBodyLimitRejection_ReturnsPayloadTooLarge()
    {
        await using var host = await StartHostAsync();
        var context = CreateFormContext(new ThrowingStream(
            () => new BadHttpRequestException("request too large", StatusCodes.Status413PayloadTooLarge)));

        await host.Handler.HandleAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Test]
    public async Task UnreadableBody_ReturnsBadRequest()
    {
        await using var host = await StartHostAsync();
        var ioFailure = CreateFormContext(new ThrowingStream(() => new IOException("read failed")));
        var dataFailure = CreateFormContext(new ThrowingStream(() => new InvalidDataException("bad form")));

        await host.Handler.HandleAsync(ioFailure);
        await host.Handler.HandleAsync(dataFailure);

        ioFailure.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        dataFailure.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext CreateFormContext(Stream body, bool readOnlySizeFeature = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = body;

        if (readOnlySizeFeature)
        {
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(new ReadOnlyMaxRequestBodySizeFeature());
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Stream FormBody(string body) => new MemoryStream(Encoding.UTF8.GetBytes(body));

    // ── Logging ─────────────────────────────────────────────────────────────

    [Test]
    public async Task RejectionLogging_NeverContainsTheLogoutToken()
    {
        await using var host = await StartHostAsync();
        var valid = CreateLogoutToken();
        var tampered = valid[..^2] + (valid[^2] == 'A' ? "B" : "A");
        var withNonce = _tokens.CreateLogoutToken(LogoutTokenFactory.CreatePayload(sid: "sid-1", nonce: "n-1"));

        await PostTokenAsync(host.Client, valid);
        await PostTokenAsync(host.Client, tampered);
        await PostTokenAsync(host.Client, withNonce);
        await PostTokenAsync(host.Client, valid); // replay

        var messages = host.LogMessages.ToList();

        messages.Should().Contain(message => message.Contains("rejected"));

        foreach (var message in messages)
        {
            message.Should().NotContain(valid);
            message.Should().NotContain(tampered);
            message.Should().NotContain(withNonce);
        }
    }

    // Framework loggers never see a request body, so these component loggers
    // are the only places the token could be written.
    private static IEnumerable<string> CollectedLogMessages(EndpointHost host)
    {
        foreach (var logger in new ILogger[] { host.ValidatorLogger, host.HandlerLogger, host.CoordinatorLogger })
        {
            foreach (var call in logger.ReceivedCalls())
            {
                if (call.GetMethodInfo().Name == nameof(ILogger.Log))
                {
                    yield return call.GetArguments()[2]?.ToString() ?? "";
                }
            }
        }
    }

    private static IEnumerable<string> WarningMessages(ILogger logger)
        => logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && Equals(call.GetArguments()[0], LogLevel.Warning))
            .Select(call => call.GetArguments()[2]?.ToString() ?? "");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met within " + timeout + ".");
    }

    private sealed class EndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly RequestLifetime _lifetime;

        private EndpointHost(
            WebApplication app,
            HttpClient client,
            AppSessionCoordinator coordinator,
            RequestLifetime lifetime,
            ILogger<BackChannelLogoutValidator> validatorLogger,
            ILogger<BackChannelLogoutHandler> handlerLogger,
            ILogger<AppSessionCoordinator> coordinatorLogger)
        {
            _app = app;
            _lifetime = lifetime;
            Client = client;
            Coordinator = coordinator;
            ValidatorLogger = validatorLogger;
            HandlerLogger = handlerLogger;
            CoordinatorLogger = coordinatorLogger;
        }

        public HttpClient Client { get; }

        public AppSessionCoordinator Coordinator { get; }

        public ILogger<BackChannelLogoutValidator> ValidatorLogger { get; }

        public ILogger<BackChannelLogoutHandler> HandlerLogger { get; }

        public ILogger<AppSessionCoordinator> CoordinatorLogger { get; }

        public IEnumerable<string> LogMessages => CollectedLogMessages(this);

        public BackChannelLogoutHandler Handler
            => _app.Services.GetRequiredService<BackChannelLogoutHandler>();

        public BackChannelLogoutReplayCache ReplayCache
            => _app.Services.GetRequiredService<BackChannelLogoutReplayCache>();

        // The token teardown must never run on, so tests can prove it does not.
        public CancellationToken LastRequestAborted => _lifetime.RequestAborted;

        public static async Task<EndpointHost> StartAsync(
            LogoutTokenFactory tokens,
            TimeSpan? teardownTimeout = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();

            builder.Services.AddSingleton(TimeProvider.System);

            // The validator reads the options off the named scheme, exactly as
            // the production OIDC handler registers them.
            builder.Services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options =>
                {
                    options.ClientId = LogoutTokenFactory.ClientId;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        tokens.CreateConfiguration());
                });

            if (teardownTimeout is { } timeout)
            {
                builder.Services.Configure<BackChannelLogoutHandlerOptions>(
                    options => options.TeardownTimeout = timeout);
            }

            var coordinatorLogger = Substitute.For<ILogger<AppSessionCoordinator>>();
            var coordinator = new AppSessionCoordinator(
                TimeProvider.System, TimeSpan.FromHours(8), coordinatorLogger);
            builder.Services.AddSingleton(coordinator);
            builder.Services.AddSingleton<BackChannelLogoutReplayCache>();
            builder.Services.AddSingleton<BackChannelLogoutValidator>();
            builder.Services.AddSingleton<BackChannelLogoutHandler>();

            var validatorLogger = Substitute.For<ILogger<BackChannelLogoutValidator>>();
            var handlerLogger = Substitute.For<ILogger<BackChannelLogoutHandler>>();
            builder.Services.AddSingleton(validatorLogger);
            builder.Services.AddSingleton(handlerLogger);

            // Same gates Program.cs puts in front of every endpoint.
            builder.Services.AddAntiforgery();
            builder.Services.AddAuthorization();

            var lifetime = new RequestLifetime();
            var app = builder.Build();
            app.UseRouting();
            app.Use(async (context, next) =>
            {
                lifetime.RequestAborted = context.RequestAborted;
                await next();
            });
            app.UseAuthorization();
            app.UseAntiforgery();
            app.MapOidcBackChannelLogout();

            await app.StartAsync();

            return new EndpointHost(
                app, app.GetTestClient(), coordinator, lifetime, validatorLogger, handlerLogger,
                coordinatorLogger);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class RequestLifetime
    {
        public CancellationToken RequestAborted { get; set; }
    }

    private sealed class ReadOnlyMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => true;

        public long? MaxRequestBodySize
        {
            get => null;
            set => throw new NotSupportedException("The server owns this limit.");
        }
    }

    private sealed class ThrowingStream : Stream
    {
        private readonly Func<Exception> _failure;

        public ThrowingStream(Func<Exception> failure) => _failure = failure;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw _failure();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException<int>(_failure());

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(byte[] payload) => _inner = new MemoryStream(payload);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // Waits on the application's teardown token, so the handler's own budget is
    // what ends the wait.
    private sealed class StuckTeardownHandler : ICircuitTeardownHandler
    {
        public Task TeardownAsync(bool notifyForceLogin = true, CancellationToken cancellationToken = default)
            => Task.Delay(Timeout.Infinite, cancellationToken);

        public Task TeardownAsync(
            Func<bool> shouldNotifyForceLogin,
            CancellationToken cancellationToken = default)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Outlives the handler's wait and only then fails, so its fault reaches the
    // log after the response has already been sent.
    private sealed class LateFaultTeardownHandler : ICircuitTeardownHandler
    {
        public Task TeardownAsync(bool notifyForceLogin = true, CancellationToken cancellationToken = default)
            => FaultAfterTheResponse(cancellationToken);

        public Task TeardownAsync(
            Func<bool> shouldNotifyForceLogin,
            CancellationToken cancellationToken = default)
            => FaultAfterTheResponse(cancellationToken);

        private static async Task FaultAfterTheResponse(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("late cleanup fault");
        }
    }

    // Never returns and ignores the token: only a revocation that starts every
    // session up front can finish a request that touches it.
    private sealed class BlockingTeardownHandler : ICircuitTeardownHandler
    {
        private readonly TaskCompletionSource _blocked = new();

        public bool Entered { get; private set; }

        public Task TeardownAsync(bool notifyForceLogin = true, CancellationToken cancellationToken = default)
            => Enter();

        public Task TeardownAsync(
            Func<bool> shouldNotifyForceLogin,
            CancellationToken cancellationToken = default)
            => Enter();

        private Task Enter()
        {
            Entered = true;
            return _blocked.Task;
        }
    }

    private sealed class ObservingTeardownHandler : ICircuitTeardownHandler
    {
        private readonly Action<CancellationToken> _observe;

        public ObservingTeardownHandler(Action<CancellationToken> observe) => _observe = observe;

        public Task TeardownAsync(bool notifyForceLogin = true, CancellationToken cancellationToken = default)
        {
            _observe(cancellationToken);
            return Task.CompletedTask;
        }

        public Task TeardownAsync(
            Func<bool> shouldNotifyForceLogin,
            CancellationToken cancellationToken = default)
        {
            _observe(cancellationToken);
            return Task.CompletedTask;
        }
    }
}
