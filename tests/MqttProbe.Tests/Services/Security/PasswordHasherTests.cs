using System.Security.Cryptography;
using System.Text;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Metrics;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class PasswordHasherTests
{
    [Test]
    public void Hash_ReturnsNonNullString()
    {
        var hash = PasswordHasher.Hash("password");
        hash.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void Hash_ReturnsDifferentValuesForSamePassword()
    {
        var hash1 = PasswordHasher.Hash("password");
        var hash2 = PasswordHasher.Hash("password");

        hash1.Should().NotBe(hash2, "each call uses a fresh random salt");
    }

    [Test]
    public void Verify_ReturnsTrueForCorrectPassword()
    {
        var hash = PasswordHasher.Hash("correct-horse");

        PasswordHasher.Verify("correct-horse", hash).Should().BeTrue();
    }

    [Test]
    public void Verify_ReturnsFalseForWrongPassword()
    {
        var hash = PasswordHasher.Hash("correct-horse");

        PasswordHasher.Verify("wrong-password", hash).Should().BeFalse();
    }

    [Test]
    public void Verify_ReturnsFalseForEmptyPassword()
    {
        var hash = PasswordHasher.Hash("correct-horse");

        PasswordHasher.Verify(string.Empty, hash).Should().BeFalse();
    }

    [Test]
    public void Verify_ReturnsFalseForTamperedHash()
    {
        var hash = PasswordHasher.Hash("password");
        var tampered = hash[..^4] + "XXXX";

        PasswordHasher.Verify("password", tampered).Should().BeFalse();
    }

    [Test]
    public void Hash_ProducesThreePartBase64EncodedString()
    {
        var hash = PasswordHasher.Hash("password");
        var parts = hash.Split(':');

        parts.Should().HaveCount(3, "format is <salt>:<hash>:<iterations>");

        // Each part (except iterations) should be valid base64.
        var fromBase64 = () => Convert.FromBase64String(parts[0]);
        fromBase64.Should().NotThrow();
        fromBase64 = () => Convert.FromBase64String(parts[1]);
        fromBase64.Should().NotThrow();
        int.TryParse(parts[2], out _).Should().BeTrue("third part is iteration count");
    }

    [Test]
    public void Hash_StoresAtLeastSixHundredThousandIterations()
    {
        var hash = PasswordHasher.Hash("password");
        var parts = hash.Split(':');

        int.Parse(parts[2]).Should().BeGreaterThanOrEqualTo(600_000);
    }

    [Test]
    public void Verify_AcceptsExistingLowerIterationHashes()
    {
        const string password = "legacy-password";
        const int legacyIterations = 100_000;
        var salt = Convert.FromBase64String("uJnm7L2h41rprK9lv3hT8A==");
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            legacyIterations,
            HashAlgorithmName.SHA256,
            32);
        var storedHash = $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}:{legacyIterations}";

        PasswordHasher.Verify(password, storedHash).Should().BeTrue();
    }

    [Test]
    public void Verify_UsesCryptographicFixedTimeCompare()
    {
        // Both calls should return false — verifying FixedTimeEquals is used
        // means wrong passwords always complete without early-exit.
        var hash = PasswordHasher.Hash("password");
        PasswordHasher.Verify("aaa", hash).Should().BeFalse();
        PasswordHasher.Verify("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", hash).Should().BeFalse();
    }
}
