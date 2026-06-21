using System.Xml.Linq;
using MqttProbe.Web.Services;

namespace MqttProbe.Shared.Tests.Services;

[TestFixture]
public class AesKeyDecryptorTests
{
    private static readonly byte[] Kek = new byte[32];

    [Test]
    public void Decrypt_MissingIvElement_ThrowsInvalidOperationException()
    {
        var decryptor = new AesKeyDecryptor(Kek);
        var element = new XElement("EncryptedKey",
            new XElement("CipherData", Convert.ToBase64String(new byte[16])));

        var act = () => decryptor.Decrypt(element);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IV*");
    }

    [Test]
    public void Decrypt_MissingCipherDataElement_ThrowsInvalidOperationException()
    {
        var decryptor = new AesKeyDecryptor(Kek);
        var element = new XElement("EncryptedKey",
            new XElement("IV", Convert.ToBase64String(new byte[16])));

        var act = () => decryptor.Decrypt(element);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CipherData*");
    }
}
