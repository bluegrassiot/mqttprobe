using System.Security.Cryptography;
using System.Text;
using MqttProbe.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class CertificateAssetBlobCodecTests
{
    private static readonly byte[] _encryptionKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Test]
    public void BuildHeader_UsesAsciiIdsAndStoredVersion()
    {
        const string assetId = "11111111-2222-3333-4444-555555555555";
        var ownerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var header = CertificateAssetBlobCodec.BuildHeader(assetId, ownerId);

        header.Length.Should().Be(73);
        Encoding.ASCII.GetString(header, 0, 36).Should().Be(assetId);
        Encoding.ASCII.GetString(header, 36, 36).Should().Be(ownerId.ToString("D"));
        header[72].Should().Be(1);
    }

    [Test]
    public void HeaderLengthChecks_UseThePersistedMinimums()
    {
        CertificateAssetBlobCodec.HasMinimumHeaderLength(new byte[72]).Should().BeFalse();
        CertificateAssetBlobCodec.HasMinimumHeaderLength(new byte[73]).Should().BeTrue();
        CertificateAssetBlobCodec.HasMinimumBlobLength(new byte[100]).Should().BeFalse();
        CertificateAssetBlobCodec.HasMinimumBlobLength(new byte[101]).Should().BeTrue();
    }

    [Test]
    public void TryParseHeader_RejectsTruncatedAndInvalidIds()
    {
        CertificateAssetBlobCodec.TryParseHeader(new byte[72], out _).Should().BeFalse();

        var invalidAsset = CertificateAssetBlobCodec.BuildHeader(
            "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        Encoding.ASCII.GetBytes("not-a-guid").CopyTo(invalidAsset, 0);
        CertificateAssetBlobCodec.TryParseHeader(invalidAsset, out _).Should().BeFalse();

        var invalidOwner = CertificateAssetBlobCodec.BuildHeader(
            "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        Encoding.ASCII.GetBytes("not-a-guid").CopyTo(invalidOwner, 36);
        CertificateAssetBlobCodec.TryParseHeader(invalidOwner, out _).Should().BeFalse();
    }

    [Test]
    public void TryParseHeader_ReturnsStoredVersionWithoutApplyingVersionPolicy()
    {
        const string assetId = "11111111-2222-3333-4444-555555555555";
        var ownerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var headerBytes = CertificateAssetBlobCodec.BuildHeader(assetId, ownerId);
        headerBytes[72] = 99;

        CertificateAssetBlobCodec.TryParseHeader(headerBytes, out var header).Should().BeTrue();
        header.AssetId.Should().Be(assetId);
        header.Owner.Should().Be(ownerId.ToString("D"));
        header.Version.Should().Be(99);
    }

    [Test]
    public void TryReadOwner_IsOwnerOnlyAndRejectsMalformedHeaders()
    {
        var ownerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var header = CertificateAssetBlobCodec.BuildHeader(
            "11111111-2222-3333-4444-555555555555", ownerId);
        Encoding.ASCII.GetBytes("not-a-guid").CopyTo(header, 0);

        CertificateAssetBlobCodec.TryReadOwner(header, out var parsedOwner).Should().BeTrue();
        parsedOwner.Should().Be(ownerId);
        CertificateAssetBlobCodec.TryReadOwner(new byte[72], out _).Should().BeFalse();

        Encoding.ASCII.GetBytes("not-a-guid").CopyTo(header, 36);
        CertificateAssetBlobCodec.TryReadOwner(header, out _).Should().BeFalse();
    }

    [Test]
    public void BuildAad_UsesTheExactUtf8EnvelopeText()
    {
        var aad = CertificateAssetBlobCodec.BuildAad(
            "asset", "header-asset", "header-owner", 7);

        aad.Should().BeEquivalentTo(Encoding.UTF8.GetBytes("asset|header-asset|header-owner|7"));
    }

    [Test]
    public void Encrypt_UsesFreshNoncesAndDecryptRestoresPlaintext()
    {
        var plaintext = Encoding.UTF8.GetBytes("certificate payload");
        var aad = Encoding.UTF8.GetBytes("asset|asset|owner|1");

        var first = CertificateAssetBlobCodec.Encrypt(_encryptionKey, plaintext, aad);
        var second = CertificateAssetBlobCodec.Encrypt(_encryptionKey, plaintext, aad);

        first.Nonce.Length.Should().Be(12);
        first.Tag.Length.Should().Be(16);
        first.Nonce.Should().NotBeEquivalentTo(second.Nonce);
        CertificateAssetBlobCodec.TryDecrypt(_encryptionKey, first, aad, out var decrypted).Should().BeTrue();
        decrypted.Should().BeEquivalentTo(plaintext);
    }

    [Test]
    public void TryDecrypt_ReturnsFalseForTamperedCiphertextTagAndAad()
    {
        var plaintext = Encoding.UTF8.GetBytes("certificate payload");
        var aad = Encoding.UTF8.GetBytes("asset|asset|owner|1");
        var encrypted = CertificateAssetBlobCodec.Encrypt(_encryptionKey, plaintext, aad);

        var ciphertext = encrypted.Ciphertext.ToArray();
        ciphertext[0] ^= 1;
        CertificateAssetBlobCodec.TryDecrypt(
            _encryptionKey,
            new CertificateAssetBlobParts(encrypted.Nonce, ciphertext, encrypted.Tag),
            aad,
            out _).Should().BeFalse();

        var tag = encrypted.Tag.ToArray();
        tag[0] ^= 1;
        CertificateAssetBlobCodec.TryDecrypt(
            _encryptionKey,
            new CertificateAssetBlobParts(encrypted.Nonce, encrypted.Ciphertext, tag),
            aad,
            out _).Should().BeFalse();

        CertificateAssetBlobCodec.TryDecrypt(
            _encryptionKey,
            encrypted,
            Encoding.UTF8.GetBytes("asset|asset|owner|2"),
            out _).Should().BeFalse();
    }

    [Test]
    public void TryVerify_ReturnsTrueForIntactPartsAndFalseForTamperedParts()
    {
        var aad = Encoding.UTF8.GetBytes("asset|asset|owner|1");
        var encrypted = CertificateAssetBlobCodec.Encrypt(_encryptionKey, [1, 2, 3], aad);

        CertificateAssetBlobCodec.TryVerify(_encryptionKey, encrypted, aad).Should().BeTrue();

        var tamperedTag = encrypted.Tag.ToArray();
        tamperedTag[^1] ^= 1;
        CertificateAssetBlobCodec.TryVerify(
            _encryptionKey,
            new CertificateAssetBlobParts(encrypted.Nonce, encrypted.Ciphertext, tamperedTag),
            aad).Should().BeFalse();
    }

    [Test]
    public void TrySplit_RejectsTruncatedPayloadAndAssembleRoundTripsExactly()
    {
        var header = CertificateAssetBlobCodec.BuildHeader(
            "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        var parts = CertificateAssetBlobCodec.Encrypt(
            _encryptionKey, Encoding.UTF8.GetBytes("payload"), Encoding.UTF8.GetBytes("aad"));
        var blob = CertificateAssetBlobCodec.Assemble(header, parts);

        CertificateAssetBlobCodec.TrySplit(blob[..100], out _).Should().BeFalse();
        CertificateAssetBlobCodec.TrySplit(blob, out var split).Should().BeTrue();
        split.Nonce.Should().BeEquivalentTo(parts.Nonce);
        split.Ciphertext.Should().BeEquivalentTo(parts.Ciphertext);
        split.Tag.Should().BeEquivalentTo(parts.Tag);
        CertificateAssetBlobCodec.Assemble(header, split).Should().BeEquivalentTo(blob);
    }

    [Test]
    public void EnvelopeKey_UsesThePersistedPrefix()
    {
        CertificateAssetBlobCodec.EnvelopeKey("asset-id").Should().Be("cert-env-asset-id");
    }

    [Test]
    public void LiteralCompatibilityBlob_UsesTheExistingPersistedFormat()
    {
        const string assetId = "11111111-2222-3333-4444-555555555555";
        const string owner = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const byte version = 1;
        var plaintext = Encoding.UTF8.GetBytes("literal certificate payload");
        var aad = Encoding.UTF8.GetBytes("11111111-2222-3333-4444-555555555555|11111111-2222-3333-4444-555555555555|aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee|1");
        var nonce = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(_encryptionKey, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        var expectedHeader = new byte[73];
        Encoding.ASCII.GetBytes(assetId).CopyTo(expectedHeader, 0);
        Encoding.ASCII.GetBytes(owner).CopyTo(expectedHeader, 36);
        expectedHeader[72] = version;
        var expectedBlob = new byte[73 + 12 + plaintext.Length + 16];
        Buffer.BlockCopy(expectedHeader, 0, expectedBlob, 0, 73);
        Buffer.BlockCopy(nonce, 0, expectedBlob, 73, 12);
        Buffer.BlockCopy(ciphertext, 0, expectedBlob, 73 + 12, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, expectedBlob, expectedBlob.Length - 16, 16);

        CertificateAssetBlobCodec.BuildHeader(assetId, Guid.Parse(owner))
            .Should().BeEquivalentTo(expectedHeader);
        CertificateAssetBlobCodec.BuildAad(assetId, assetId, owner, version)
            .Should().BeEquivalentTo(aad);
        CertificateAssetBlobCodec.Assemble(
            expectedHeader, new CertificateAssetBlobParts(nonce, ciphertext, tag))
            .Should().BeEquivalentTo(expectedBlob);
        CertificateAssetBlobCodec.TrySplit(expectedBlob, out var parts).Should().BeTrue();
        CertificateAssetBlobCodec.TryParseHeader(expectedBlob, out var header).Should().BeTrue();
        header.AssetId.Should().Be(assetId);
        header.Owner.Should().Be(owner);
        header.Version.Should().Be(version);
        CertificateAssetBlobCodec.TryDecrypt(_encryptionKey, parts, aad, out var decrypted).Should().BeTrue();
        decrypted.Should().BeEquivalentTo(plaintext);
        CertificateAssetBlobCodec.TryVerify(_encryptionKey, parts, aad).Should().BeTrue();
        CertificateAssetBlobCodec.EnvelopeKey("asset-id").Should().Be("cert-env-asset-id");
    }
}
