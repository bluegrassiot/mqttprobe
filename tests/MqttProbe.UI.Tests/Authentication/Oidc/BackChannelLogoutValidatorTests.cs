using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class BackChannelLogoutValidatorTests
{
    private LogoutTokenFactory _tokens = null!;
    private ServiceProvider? _serviceProvider;

    [SetUp]
    public void SetUp() => _tokens = new LogoutTokenFactory();

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        _tokens.Dispose();
    }

    // Real DI: Program.cs configures OpenIdConnectOptions under the named
    // scheme, so the validator must resolve through IOptionsMonitor.Get with
    // that name rather than the never-configured default-name options.
    private BackChannelLogoutValidator CreateValidator(Action<OpenIdConnectOptions>? configure = null)
    {
        _serviceProvider?.Dispose();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.ClientId = LogoutTokenFactory.ClientId;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                _tokens.CreateConfiguration());
        });

        if (configure is not null)
        {
            services.Configure(OpenIdConnectDefaults.AuthenticationScheme, configure);
        }

        services.AddSingleton<BackChannelLogoutValidator>();
        _serviceProvider = services.BuildServiceProvider();

        return _serviceProvider.GetRequiredService<BackChannelLogoutValidator>();
    }

    private Task<BackChannelLogoutValidationResult> ValidateAsync(
        BackChannelLogoutValidator validator,
        string payloadJson,
        RSA? signingRsa = null)
    {
        return validator.ValidateAsync(
            _tokens.CreateLogoutToken(payloadJson, signingRsa),
            CancellationToken.None);
    }

    // ── Accepts a well-formed token ─────────────────────────────────────────

    [Test]
    public async Task ValidLogoutToken_IsAcceptedWithSessionClaims()
    {
        var validator = CreateValidator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(
            sid: "sid-1", issuedAt: now, expiresAt: now + 120));

        result.IsValid.Should().BeTrue(result.Reason);
        result.Issuer.Should().Be(LogoutTokenFactory.Issuer);
        result.Subject.Should().Be("user-123");
        result.Sid.Should().Be("sid-1");
        result.TokenId.Should().Be("jti-1");
        result.ExpiresAt.ToUnixTimeSeconds().Should().Be(now + 120);
    }

    [Test]
    public async Task ValidLogoutToken_WithoutSid_StillAccepted()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload());

        result.IsValid.Should().BeTrue(result.Reason);
        result.Sid.Should().BeNull();
        result.Subject.Should().Be("user-123");
    }

    [Test]
    public async Task ValidLogoutToken_WithSidOnly_StillAccepted()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(subject: null, sid: "sid-1"));

        result.IsValid.Should().BeTrue(result.Reason);
        result.Subject.Should().BeNull();
        result.Sid.Should().Be("sid-1");
    }

    // ── Signature and standard claim validation ──────────────────────────────

    [Test]
    public async Task TokenSignedByUntrustedKey_IsRejected()
    {
        using var rogueKey = RSA.Create(2048);
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(), rogueKey);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_signature");
    }

    [Test]
    public async Task UnsignedToken_IsRejected()
    {
        var validator = CreateValidator();
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\"}");
        var payload = Base64UrlEncoder.Encode(LogoutTokenFactory.CreatePayload());
        var token = $"{header}.{payload}.";

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().BeOneOf("disallowed_algorithm", "invalid_signature", "malformed_token");
    }

    [Test]
    public async Task HmacSignedToken_IsRejected()
    {
        var validator = CreateValidator();
        var header = Base64UrlEncoder.Encode("{\"alg\":\"HS256\"}");
        var payload = Base64UrlEncoder.Encode(LogoutTokenFactory.CreatePayload());
        var signingInput = header + "." + payload;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(LogoutTokenFactory.ClientId));
        var signature = Base64UrlEncoder.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));

        var result = await validator.ValidateAsync($"{signingInput}.{signature}", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("disallowed_algorithm");
    }

    [Test]
    public async Task MetadataAdvertisingOnlyUnsupportedAlgorithms_IsRejected()
    {
        var configuration = _tokens.CreateConfiguration();
        configuration.IdTokenSigningAlgValuesSupported.Add("HS256");
        var validator = CreateValidator(options =>
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload());

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("unsupported_signing_algorithms");
    }

    [Test]
    public async Task TokenFromDifferentIssuer_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(
            validator, LogoutTokenFactory.CreatePayload(issuer: "https://other-idp.example.com"));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_issuer");
    }

    [Test]
    public async Task TokenForAnotherAudience_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(
            validator, LogoutTokenFactory.CreatePayload(audience: "some-other-client"));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_audience");
    }

    [Test]
    public async Task ExpiredToken_IsRejected()
    {
        var validator = CreateValidator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(
            issuedAt: now - 3600, expiresAt: now - 3000));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("token_expired");
    }

    [Test]
    public async Task TokenExpiringInsideClockSkew_IsRejected()
    {
        var validator = CreateValidator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Within the signature layer's clock skew but past exp; replay entries
        // are held until exp, so this must not be accepted.
        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(
            issuedAt: now - 60, expiresAt: now - 30));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("token_expired");
    }

    [Test]
    public async Task MissingMetadata_IsRejected()
    {
        var validator = CreateValidator(options =>
        {
            options.ConfigurationManager = null!;
            options.Configuration = null;
        });

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload());

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("metadata_unavailable");
    }

    // ── Back-channel logout shape ────────────────────────────────────────────

    [Test]
    public async Task MissingIat_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(includeIat: false));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("missing_iat");
    }

    [Test]
    public async Task MissingExp_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(includeExp: false));

        result.IsValid.Should().BeFalse();
        // The lifetime layer requires exp before the shape check runs.
        result.Reason.Should().BeOneOf("invalid_lifetime", "missing_exp");
    }

    [Test]
    public async Task MissingJti_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(tokenId: null));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("missing_jti");
    }

    [Test]
    public async Task MissingEvents_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(includeEvents: false));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_events");
    }

    [Test]
    public async Task EventsWithoutBackChannelLogoutMember_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(
            includeEvents: false,
            extraJson: "\"events\":{\"https://example.com/other-event\":{}}");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_events");
    }

    [Test]
    public async Task EventsNotAnObject_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(includeEvents: false, extraJson: "\"events\":[]");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_events");
    }

    [Test]
    public async Task LogoutEventValueNotAnObject_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(
            includeEvents: false,
            extraJson: "\"events\":{\"http://schemas.openid.net/event/backchannel-logout\":\"nope\"}");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_events");
    }

    [Test]
    public async Task DuplicateNestedEventsKey_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(
            includeEvents: false,
            extraJson: "\"events\":{" +
                "\"http://schemas.openid.net/event/backchannel-logout\":{}," +
                "\"http://schemas.openid.net/event/backchannel-logout\":{}}");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("malformed_payload");
    }

    [Test]
    public async Task DuplicateClaimInsideEventValue_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(
            includeEvents: false,
            extraJson: "\"events\":{\"http://schemas.openid.net/event/backchannel-logout\":" +
                "{\"k\":1,\"k\":2}}");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("malformed_payload");
    }

    [Test]
    public async Task NonceClaim_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(nonce: "n-1"));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("nonce_present");
    }

    // ── Present-but-invalid optional session claims ──────────────────────────

    [Test]
    public async Task PresentEmptySid_IsRejected()
    {
        var validator = CreateValidator();

        // A malformed sid must not degrade into "no sid" and widen the logout
        // to every session of the subject.
        var payload = LogoutTokenFactory.CreatePayload(sid: null, extraJson: "\"sid\":\"\"");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_sid");
        result.Subject.Should().BeNull();
    }

    [Test]
    public async Task PresentNonStringSid_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(sid: null, extraJson: "\"sid\":123");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_sid");
    }

    [Test]
    public async Task PresentEmptySubject_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(subject: null, extraJson: "\"sub\":\"\"");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_subject");
    }

    [Test]
    public async Task PresentNonStringSubject_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(subject: null, extraJson: "\"sub\":42");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_subject");
    }

    [Test]
    public async Task NeitherSubNorSid_IsRejected()
    {
        var validator = CreateValidator();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(subject: null, sid: null));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("missing_session_claims");
    }

    // ── Freshness bounds ─────────────────────────────────────────────────────

    [Test]
    public async Task IssuedInFuture_IsRejected()
    {
        var validator = CreateValidator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(
            issuedAt: now + 600, expiresAt: now + 720));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_iat");
    }

    [Test]
    public async Task ExpiringBeforeIssued_IsRejected()
    {
        var validator = CreateValidator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(
            issuedAt: now + 60, expiresAt: now + 30));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_lifetime");
    }

    [Test]
    public async Task OverlongTokenAge_IsRejected()
    {
        var validator = CreateValidator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Unexpired and within the lifetime cap, but too old to be a fresh
        // logout: 11 minutes since issuance.
        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(
            issuedAt: now - 660, expiresAt: now + 60));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("token_too_old");
    }

    [Test]
    public async Task OverlongTokenLifetime_IsRejected()
    {
        var validator = CreateValidator();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = await ValidateAsync(validator, LogoutTokenFactory.CreatePayload(
            issuedAt: now - 300, expiresAt: now + 1900));

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("token_lifetime_too_long");
    }

    // ── Duplicate claims ─────────────────────────────────────────────────────

    [Test]
    public async Task DuplicateClaim_IsRejected()
    {
        var validator = CreateValidator();
        var payload = LogoutTokenFactory.CreatePayload(
            tokenId: null, extraJson: "\"jti\":\"first\",\"jti\":\"second\"");

        var result = await ValidateAsync(validator, payload);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("malformed_payload");
    }

    [Test]
    public async Task BlankToken_IsRejected()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync("  ", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("missing_token");
    }
}
