using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using MqttProbe.Core.Services.Security;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class AuthenticationTicketFactoryTests
{
    private static JsonElement ParsePayload(string json) => JsonDocument.Parse(json).RootElement;

    // ── Ticket creation ──────────────────────────────────────────────────────

    [Test]
    public void Create_ReturnsAuthenticationTicket()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Should().NotBeNull();
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    [Test]
    public void Create_SetsAbsoluteExpiry()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Properties.ExpiresUtc.Should().Be(expiresAt);
    }

    [Test]
    public void Create_SetsIssuedUtc()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Properties.IssuedUtc.Should().Be(issuedAt);
    }

    // ── Token retention ──────────────────────────────────────────────────────

    [Test]
    public void Create_StoresIdTokenOnly()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Properties.GetTokenValue("id_token").Should().Be("id-token-abc");
    }

    [Test]
    public void Create_DoesNotStoreAccessToken()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Properties.GetTokenValue("access_token").Should().BeNull();
    }

    [Test]
    public void Create_DoesNotStoreRefreshToken()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Properties.GetTokenValue("refresh_token").Should().BeNull();
    }

    [Test]
    public void Create_DoesNotStoreCode()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Properties.GetTokenValue("code").Should().BeNull();
    }

    [Test]
    public void Create_DoesNotStoreOriginalClaims()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","email":"john@example.com"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        // No original token claims should be stored in properties
        ticket.Properties.Items.Should().NotContainKey("email");
    }

    // ── Principal claims ─────────────────────────────────────────────────────

    [Test]
    public void Create_PrincipalHasDisplayName()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Principal.Identity!.Name.Should().Be("John Doe");
    }

    [Test]
    public void Create_PrincipalIsInRoleAdmin()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Principal.IsInRole(AppRoles.Admin).Should().BeTrue();
    }

    // ── SaveTokens is false ──────────────────────────────────────────────────

    [Test]
    public void Create_OnlyIdTokenStored()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        // Only id_token should be present, no other tokens
        ticket.Properties.GetTokenValue("id_token").Should().NotBeNull();
        ticket.Properties.GetTokenValue("access_token").Should().BeNull();
        ticket.Properties.GetTokenValue("refresh_token").Should().BeNull();
    }

    // ── Reuses minimal principal ─────────────────────────────────────────────

    [Test]
    public void Create_HasExactlySixClaims()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");
        var issuedAt = new DateTimeOffset(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        var expiresAt = issuedAt.AddHours(8);

        var ticket = AuthenticationTicketFactory.Create(payload, "session-1", issuedAt, expiresAt, "id-token-abc");

        ticket.Principal.Claims.Should().HaveCount(6);
    }
}
