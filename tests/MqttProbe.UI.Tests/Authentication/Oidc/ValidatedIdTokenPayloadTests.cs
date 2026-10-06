using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class ValidatedIdTokenPayloadTests
{
    // Helper to create a proper JWT string from a JSON payload
    private static string CreateTestJwt(string payloadJson)
    {
        // Create a minimal JWT: header.payload.signature
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(payloadJson);
        var signature = Base64UrlEncoder.Encode("test-signature");
        return $"{header}.{payload}.{signature}";
    }

    // ── Creation from actual JsonWebToken ────────────────────────────────────

    [Test]
    public void CreateFromToken_ValidToken_CreatesInstance()
    {
        var jwt = CreateTestJwt("""{"iss":"https://idp.example.com","sub":"user-123","groups":"mqttprobe-users"}""");
        var token = new JsonWebToken(jwt);

        using var payload = ValidatedIdTokenPayload.CreateFromToken(token);

        payload.Root.GetProperty("iss").GetString().Should().Be("https://idp.example.com");
    }

    [Test]
    public void CreateFromToken_WithClaims_PreservesAllClaims()
    {
        var jwt = CreateTestJwt("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","groups":["admin","users"]}""");
        var token = new JsonWebToken(jwt);

        using var payload = ValidatedIdTokenPayload.CreateFromToken(token);

        payload.Root.GetProperty("iss").GetString().Should().Be("https://idp.example.com");
        payload.Root.GetProperty("sub").GetString().Should().Be("user-123");
        payload.Root.GetProperty("name").GetString().Should().Be("John Doe");
    }

    [Test]
    public void CreateFromToken_NullToken_Throws()
    {
        var act = () => ValidatedIdTokenPayload.CreateFromToken(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Creation from raw JSON (internal test seam) ──────────────────────────

    [Test]
    public void CreateFromRawJson_ValidPayload_CreatesInstance()
    {
        var json = """{"iss":"https://idp.example.com","sub":"user-123","groups":"mqttprobe-users"}""";

        using var payload = ValidatedIdTokenPayload.CreateFromRawJson(json);

        payload.Root.GetProperty("iss").GetString().Should().Be("https://idp.example.com");
    }

    [Test]
    public void CreateFromRawJson_DuplicateProperties_Throws()
    {
        var json = """{"iss":"https://idp.example.com","sub":"user-123","groups":"other","groups":"mqttprobe-users"}""";

        var act = () => ValidatedIdTokenPayload.CreateFromRawJson(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*duplicate*groups*");
    }

    [Test]
    public void CreateFromRawJson_InvalidJson_Throws()
    {
        var json = """{"iss":"https://idp.example.com","sub":}""";

        var act = () => ValidatedIdTokenPayload.CreateFromRawJson(json);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not valid*");
    }

    [Test]
    public void CreateFromRawJson_BlankJson_Throws()
    {
        var act = () => ValidatedIdTokenPayload.CreateFromRawJson("");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void CreateFromRawJson_NullJson_Throws()
    {
        var act = () => ValidatedIdTokenPayload.CreateFromRawJson(null!);

        act.Should().Throw<ArgumentException>();
    }

    // ── AdmissionEvaluator with ValidatedIdTokenPayload ──────────────────────

    [Test]
    public void IsAdmitted_WithValidatedPayload_Works()
    {
        var json = """{"iss":"https://idp.example.com","sub":"user-123","groups":"mqttprobe-users"}""";
        using var payload = ValidatedIdTokenPayload.CreateFromRawJson(json);

        var result = AdmissionEvaluator.IsAdmitted(payload, "groups", ["mqttprobe-users"]);

        result.Should().BeTrue();
    }

    [Test]
    public void GetDisplayName_WithValidatedPayload_Works()
    {
        var json = """{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""";
        using var payload = ValidatedIdTokenPayload.CreateFromRawJson(json);

        var displayName = AdmissionEvaluator.GetDisplayName(payload);

        displayName.Should().Be("John Doe");
    }

    // ── MinimalPrincipalFactory with ValidatedIdTokenPayload ──────────────────

    [Test]
    public void Create_WithValidatedPayload_Works()
    {
        var json = """{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""";
        using var payload = ValidatedIdTokenPayload.CreateFromRawJson(json);

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.Identity!.Name.Should().Be("John Doe");
        principal.Claims.Should().HaveCount(6);
    }

    // ── AuthenticationTicketFactory with ValidatedIdTokenPayload ──────────────

    [Test]
    public void CreateTicket_WithValidatedPayload_StoresOnlyIdToken()
    {
        var json = """{"iss":"https://idp.example.com","sub":"user-123"}""";
        using var payload = ValidatedIdTokenPayload.CreateFromRawJson(json);
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Properties.GetTokenValue("id_token").Should().Be("id-token-abc");
        ticket.Properties.GetTokenValue("access_token").Should().BeNull();
        ticket.Properties.GetTokenValue("refresh_token").Should().BeNull();
    }
}
