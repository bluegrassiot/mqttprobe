using System.Xml.Linq;
using MqttProbe.Web.Services;

namespace MqttProbe.Shared.Tests.Services;

[TestFixture]
public class AesKeyEncryptorTests
{
    private static readonly byte[] Kek = new byte[32]; // 256-bit zero key — valid AES-256 key for tests

    [Test]
    public void Encrypt_ThenDecrypt_ReturnsOriginalElement()
    {
        var encryptor = new AesKeyEncryptor(Kek);
        var decryptor = new AesKeyDecryptor(Kek);
        var original = new XElement("Key", "some-secret-material");

        var info = encryptor.Encrypt(original);
        var roundTripped = decryptor.Decrypt(info.EncryptedElement);

        roundTripped.ToString().Should().Be(original.ToString());
    }

    [Test]
    public void Encrypt_DecryptorType_IsAesKeyDecryptor()
    {
        var info = new AesKeyEncryptor(Kek).Encrypt(new XElement("Key", "v"));

        info.DecryptorType.Should().Be(typeof(AesKeyDecryptor));
    }

    [Test]
    public void Encrypt_ProducesNonPlaintextOutput()
    {
        var info = new AesKeyEncryptor(Kek).Encrypt(new XElement("Key", "secret-value"));

        info.EncryptedElement.ToString().Should().NotContain("secret-value");
    }

    [Test]
    public void Encrypt_ProducesUniqueIvEachCall()
    {
        var encryptor = new AesKeyEncryptor(Kek);
        var element = new XElement("Key", "same-content");

        var info1 = encryptor.Encrypt(element);
        var info2 = encryptor.Encrypt(element);

        var iv1 = (string?)info1.EncryptedElement.Element("IV");
        var iv2 = (string?)info2.EncryptedElement.Element("IV");
        iv1.Should().NotBe(iv2);
    }
}
