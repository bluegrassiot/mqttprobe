using System.Security.Claims;
using System.Text.Json;
using MqttProbe.Core.Services.Security;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class MinimalPrincipalFactoryTests
{
    private static JsonElement ParsePayload(string json) => JsonDocument.Parse(json).RootElement;

    // ── Display name fallback ────────────────────────────────────────────────

    [Test]
    public void Create_WithNameClaim_UsesNameAsDisplayName()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","preferred_username":"johnd"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("John Doe");
    }

    [Test]
    public void Create_WithoutName_UsesPreferredUsernameAsDisplayName()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","preferred_username":"johnd"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("johnd");
    }

    [Test]
    public void Create_WithoutNameAndPreferredUsername_UsesSubAsDisplayName()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("user-123");
    }

    [Test]
    public void Create_WithEmptyName_FallsBackToPreferredUsername()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"","preferred_username":"johnd"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("johnd");
    }

    [Test]
    public void Create_WithEmptyNameAndEmptyPreferredUsername_FallsBackToSub()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"","preferred_username":""}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("user-123");
    }

    [Test]
    public void Create_WithWhitespaceName_FallsBackToPreferredUsername()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"   ","preferred_username":"johnd"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("johnd");
    }

    // ── Framework name/role claim types via NameClaimType/RoleClaimType ──────

    [Test]
    public void Create_IdentityName_ReturnsDisplayName()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.Identity!.Name.Should().Be("John Doe");
    }

    [Test]
    public void Create_IsInRole_Admin_ReturnsTrue()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.IsInRole(AppRoles.Admin).Should().BeTrue();
    }

    [Test]
    public void Create_IsInRole_Operator_ReturnsFalse()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.IsInRole(AppRoles.Operator).Should().BeFalse();
    }

    [Test]
    public void Create_HasAppDisplayNameClaim()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppDisplayName)!.Value.Should().Be("John Doe");
    }

    [Test]
    public void Create_HasAppRoleClaim()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppRole)!.Value.Should().Be(AppRoles.Admin);
    }

    // ── Exact principal claims (exactly six) ─────────────────────────────────

    [Test]
    public void Create_SetsIssuerClaim()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppIssuer)!.Value.Should().Be("https://idp.example.com");
    }

    [Test]
    public void Create_SetsSubjectClaim()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-456"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppSubject)!.Value.Should().Be("user-456");
    }

    [Test]
    public void Create_SetsSessionIdClaim()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "custom-session-id", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppSessionId)!.Value.Should().Be("custom-session-id");
    }

    [Test]
    public void Create_SetsSessionExpiryClaimAsUnixSeconds()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var expiry = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", expiry);

        principal.FindFirst(AuthClaimTypes.AppSessionExpiry)!.Value.Should().Be(expiry.ToUnixTimeSeconds().ToString());
    }

    [Test]
    public void Create_SetsAdminRole()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppRole)!.Value.Should().Be(AppRoles.Admin);
    }

    [Test]
    public void Create_HasExactlySixClaims()
    {
        // iss, sub, display_name, session_id, session_expiry, role
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.Claims.Should().HaveCount(6);
    }

    // ── Session ID and expiry from caller ────────────────────────────────────

    [Test]
    public void Create_UsesProvidedSessionId()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "w4-coordinated-id", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(AuthClaimTypes.AppSessionId)!.Value.Should().Be("w4-coordinated-id");
    }

    [Test]
    public void Create_UsesProvidedSessionExpiry()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");
        var expiry = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", expiry);

        principal.FindFirst(AuthClaimTypes.AppSessionExpiry)!.Value.Should().Be(expiry.ToUnixTimeSeconds().ToString());
    }

    // ── No extra claims forwarded ────────────────────────────────────────────

    [Test]
    public void Create_DoesNotForwardExtraTokenClaims()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe","email":"john@example.com","groups":["admin","users"],"custom_claim":"should-not-appear"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst("email").Should().BeNull();
        principal.FindFirst("groups").Should().BeNull();
        principal.FindFirst("custom_claim").Should().BeNull();
    }

    // ── Authentication type ──────────────────────────────────────────────────

    [Test]
    public void Create_IdentityHasOidcAuthenticationType()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.Identity!.AuthenticationType.Should().Be("OIDC");
    }

    // ── No duplicate ClaimTypes.Name/Role ────────────────────────────────────

    [Test]
    public void Create_DoesNotHaveClaimTypesName()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(ClaimTypes.Name).Should().BeNull();
    }

    [Test]
    public void Create_DoesNotHaveClaimTypesRole()
    {
        var payload = ParsePayload("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var principal = MinimalPrincipalFactory.Create(payload, "session-1", DateTimeOffset.UtcNow.AddHours(8));

        principal.FindFirst(ClaimTypes.Role).Should().BeNull();
    }
}
