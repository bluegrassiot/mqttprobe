using System.Security.Cryptography;
using System.Text;

namespace MqttProbe.Services.Security;

internal readonly record struct CertificateAssetBlobHeader(string AssetId, string Owner, byte Version);

internal readonly record struct CertificateAssetBlobParts(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

internal static class CertificateAssetBlobCodec
{
    internal const int HeaderLength = 73;
    internal const int AssetIdOffset = 0;
    internal const int OwnerOffset = 36;
    internal const int VersionOffset = 72;
    internal const int NonceLength = 12;
    internal const int TagLength = 16;
    internal const byte Version = 1;

    public static byte[] BuildHeader(string assetId, Guid ownerConnectionId)
    {
        var header = new byte[HeaderLength];
        Encoding.ASCII.GetBytes(assetId, 0, 36, header, AssetIdOffset);
        Encoding.ASCII.GetBytes(ownerConnectionId.ToString("D"), 0, 36, header, OwnerOffset);
        header[VersionOffset] = Version;
        return header;
    }

    public static bool HasMinimumHeaderLength(ReadOnlySpan<byte> blob) =>
        blob.Length >= HeaderLength;

    public static bool HasMinimumBlobLength(ReadOnlySpan<byte> blob) =>
        blob.Length >= HeaderLength + NonceLength + TagLength;

    public static bool TryParseHeader(ReadOnlySpan<byte> blob, out CertificateAssetBlobHeader header)
    {
        header = default;
        if (!HasMinimumHeaderLength(blob))
            return false;

        var assetId = Encoding.ASCII.GetString(blob.Slice(AssetIdOffset, 36));
        var owner = Encoding.ASCII.GetString(blob.Slice(OwnerOffset, 36));
        if (!Guid.TryParse(assetId, out _) || !Guid.TryParse(owner, out _))
            return false;

        header = new CertificateAssetBlobHeader(assetId, owner, blob[VersionOffset]);
        return true;
    }

    public static bool TryReadOwner(ReadOnlySpan<byte> blob, out Guid ownerConnectionId)
    {
        ownerConnectionId = Guid.Empty;
        if (!HasMinimumHeaderLength(blob))
            return false;

        var owner = Encoding.ASCII.GetString(blob.Slice(OwnerOffset, 36));
        return Guid.TryParse(owner, out ownerConnectionId);
    }

    public static byte[] BuildAad(string assetId, string headerAssetId, string headerOwner, byte headerVersion) =>
        Encoding.UTF8.GetBytes($"{assetId}|{headerAssetId}|{headerOwner}|{headerVersion}");

    public static byte[] Assemble(ReadOnlySpan<byte> header, CertificateAssetBlobParts parts)
    {
        var blob = new byte[header.Length + parts.Nonce.Length + parts.Ciphertext.Length + parts.Tag.Length];
        var offset = 0;
        header.CopyTo(blob.AsSpan(offset));
        offset += header.Length;
        parts.Nonce.AsSpan().CopyTo(blob.AsSpan(offset));
        offset += parts.Nonce.Length;
        parts.Ciphertext.AsSpan().CopyTo(blob.AsSpan(offset));
        offset += parts.Ciphertext.Length;
        parts.Tag.AsSpan().CopyTo(blob.AsSpan(offset));
        return blob;
    }

    public static bool TrySplit(ReadOnlySpan<byte> blob, out CertificateAssetBlobParts parts)
    {
        parts = default;
        if (!HasMinimumBlobLength(blob))
            return false;

        var nonceStart = HeaderLength;
        var ciphertextStart = nonceStart + NonceLength;
        var tagStart = blob.Length - TagLength;
        parts = new CertificateAssetBlobParts(
            blob.Slice(nonceStart, NonceLength).ToArray(),
            blob.Slice(ciphertextStart, tagStart - ciphertextStart).ToArray(),
            blob.Slice(tagStart, TagLength).ToArray());
        return true;
    }

    public static CertificateAssetBlobParts Encrypt(
        byte[] encryptionKey, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(encryptionKey, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return new CertificateAssetBlobParts(nonce, ciphertext, tag);
    }

    public static bool TryDecrypt(
        byte[] encryptionKey,
        CertificateAssetBlobParts parts,
        ReadOnlySpan<byte> aad,
        out byte[] plaintext)
    {
        plaintext = [];
        try
        {
            plaintext = new byte[parts.Ciphertext.Length];
            using var aes = new AesGcm(encryptionKey, TagLength);
            aes.Decrypt(parts.Nonce, parts.Ciphertext, parts.Tag, plaintext, aad);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            plaintext = [];
            return false;
        }
    }

    public static bool TryVerify(
        byte[] encryptionKey, CertificateAssetBlobParts parts, ReadOnlySpan<byte> aad)
    {
        try
        {
            using var aes = new AesGcm(encryptionKey, TagLength);
            aes.Decrypt(parts.Nonce, parts.Ciphertext, parts.Tag, new byte[parts.Ciphertext.Length], aad);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    public static string EnvelopeKey(string assetId) => $"cert-env-{assetId}";
}
