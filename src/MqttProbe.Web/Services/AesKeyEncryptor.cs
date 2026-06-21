using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace MqttProbe.Web.Services;

public class AesKeyEncryptor(byte[] keyEncryptingKey) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        using var aes = Aes.Create();
        aes.Key = keyEncryptingKey;
        aes.GenerateIV();

        var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString());
        byte[] cipherText;
        using (var encryptor = aes.CreateEncryptor())
        {
            cipherText = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        var ivBase64 = Convert.ToBase64String(aes.IV);
        var cipherBase64 = Convert.ToBase64String(cipherText);

        var encryptedElement = new XElement("EncryptedKey",
            new XElement("CipherData", cipherBase64),
            new XElement("IV", ivBase64));

        return new EncryptedXmlInfo(encryptedElement, typeof(AesKeyDecryptor));
    }
}
