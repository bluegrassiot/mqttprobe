using System.Text.Json;
using MqttProbe.Web.Authentication;

namespace MqttProbe.UI.Tests.Authentication;

[TestFixture]
public class AdmissionEvaluatorTests
{
    // ── JsonElement payload tests ────────────────────────────────────────────

    [Test]
    public void IsAdmitted_JsonStringMatchingValue_ReturnsTrue()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":"mqttprobe-users"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeTrue();
    }

    [Test]
    public void IsAdmitted_JsonStringNonMatchingValue_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":"other-group"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonStringArrayWithMatchingValue_ReturnsTrue()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":["group-a","mqttprobe-users","group-b"]}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeTrue();
    }

    [Test]
    public void IsAdmitted_JsonStringArrayWithoutMatchingValue_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":["group-a","group-b"]}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonEmptyArray_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":[]}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonMissingClaim_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonNullClaimValue_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":null}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonObjectClaimValue_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":{"nested":"value"}}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonMixedArray_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":["mqttprobe-users",123]}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonNumberScalar_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":42}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonBooleanScalar_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":true}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonMissingIss_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"sub":"user-123","groups":"mqttprobe-users"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonMissingSub_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","groups":"mqttprobe-users"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonEmptyIss_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"","sub":"user-123","groups":"mqttprobe-users"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    // ── Case sensitivity ─────────────────────────────────────────────────────

    [Test]
    public void IsAdmitted_JsonDifferentCase_ReturnsFalse()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":"Mqttprobe-Users"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    [Test]
    public void IsAdmitted_JsonExactCase_ReturnsTrue()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":"mqttprobe-users"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeTrue();
    }

    // ── Multiple accepted values ─────────────────────────────────────────────

    [Test]
    public void IsAdmitted_JsonMultipleAcceptedValues_MatchesFirst_ReturnsTrue()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":"group-a"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["group-a", "group-b"]);

        result.Should().BeTrue();
    }

    [Test]
    public void IsAdmitted_JsonMultipleAcceptedValues_MatchesLast_ReturnsTrue()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","groups":"group-b"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["group-a", "group-b"]);

        result.Should().BeTrue();
    }

    // ── Display name selection from payload ──────────────────────────────────

    [Test]
    public void GetDisplayName_JsonWithName_ReturnsName()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","name":"John Doe"}""");

        var displayName = AdmissionEvaluator.GetDisplayName(payload.RootElement);

        displayName.Should().Be("John Doe");
    }

    [Test]
    public void GetDisplayName_JsonWithPreferredUsername_ReturnsPreferredUsername()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","preferred_username":"johnd"}""");

        var displayName = AdmissionEvaluator.GetDisplayName(payload.RootElement);

        displayName.Should().Be("johnd");
    }

    [Test]
    public void GetDisplayName_JsonWithOnlySub_ReturnsSub()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123"}""");

        var displayName = AdmissionEvaluator.GetDisplayName(payload.RootElement);

        displayName.Should().Be("user-123");
    }

    [Test]
    public void GetDisplayName_JsonWithEmptyName_FallsBackToPreferredUsername()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","name":"","preferred_username":"johnd"}""");

        var displayName = AdmissionEvaluator.GetDisplayName(payload.RootElement);

        displayName.Should().Be("johnd");
    }

    // ── Claim name case sensitivity ──────────────────────────────────────────

    [Test]
    public void IsAdmitted_JsonAdmissionClaimNameIsCaseSensitive()
    {
        var payload = JsonDocument.Parse("""{"iss":"https://idp.example.com","sub":"user-123","Groups":"mqttprobe-users"}""");

        var result = AdmissionEvaluator.IsAdmitted(payload.RootElement, "groups", ["mqttprobe-users"]);

        result.Should().BeFalse();
    }

    // ── Duplicate top-level JSON property rejection ──────────────────────────

    [Test]
    public void HasDuplicateTopLevelProperties_NoDuplicates_ReturnsFalse()
    {
        var json = """{"iss":"https://idp.example.com","sub":"user-123","groups":"mqttprobe-users"}""";

        var result = AdmissionEvaluator.HasDuplicateTopLevelProperties(json);

        result.Should().BeFalse();
    }

    [Test]
    public void HasDuplicateTopLevelProperties_DuplicateKey_ReturnsTrue()
    {
        // System.Text.Json keeps last value on duplicate keys
        var json = """{"iss":"https://idp.example.com","sub":"user-123","groups":"other","groups":"mqttprobe-users"}""";

        var result = AdmissionEvaluator.HasDuplicateTopLevelProperties(json);

        result.Should().BeTrue();
    }

    [Test]
    public void HasDuplicateTopLevelProperties_InvalidJson_ReturnsTrue()
    {
        var json = """{"iss":"https://idp.example.com","sub":}""";

        var result = AdmissionEvaluator.HasDuplicateTopLevelProperties(json);

        result.Should().BeTrue();
    }
}
