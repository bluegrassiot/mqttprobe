using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace MqttProbe.Web.Services;

public class AesKeyDecryptor(byte[] keyEncryptingKey) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var ivElement = encryptedElement.Element("IV")
            ?? throw new InvalidOperationException("Encrypted element is missing the required 'IV' child element.");
        var cipherDataElement = encryptedElement.Element("CipherData")
            ?? throw new InvalidOperationException("Encrypted element is missing the required 'CipherData' child element.");

        var iv = Convert.FromBase64String((string)ivElement);
        var cipherText = Convert.FromBase64String((string)cipherDataElement);

        using var aes = Aes.Create();
        aes.Key = keyEncryptingKey;
        aes.IV = iv;

        byte[] plaintext;
        using (var decryptor = aes.CreateDecryptor())
            plaintext = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);

        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
    }
}
