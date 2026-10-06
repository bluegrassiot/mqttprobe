using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;

namespace MqttProbe.Core.Tests.Services.Security;

[TestFixture]
public class CertificateAssetStoreTests
{
    private string _tempDir = null!;
    private InMemoryEnvelopeKeyStore _envelopeStore = null!;
    private CertificateAssetStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cert-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _envelopeStore = new InMemoryEnvelopeKeyStore();
        _store = new CertificateAssetStore(_envelopeStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());
    }

    [TearDown]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestCase(true, false, X509KeyStorageFlags.DefaultKeySet)]
    [TestCase(false, true, X509KeyStorageFlags.DefaultKeySet)]
    [TestCase(false, false, X509KeyStorageFlags.EphemeralKeySet)]
    public void GetClientCertificateLoadFlags_ReturnsPlatformCompatibleFlags(
        bool isWindows,
        bool isMacOs,
        X509KeyStorageFlags expected)
    {
        CertificateAssetStore.GetClientCertificateLoadFlags(isWindows, isMacOs)
            .Should().Be(expected);
    }

    // --- Step 4.1/4.2: Import PFX ---

    [Test]
    public async Task ImportAsync_PfxWithCorrectPassword_Succeeds()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var ownerId = Guid.NewGuid();

        var assetId = await _store.ImportAsync(ownerId, request);

        assetId.Should().NotBeNullOrEmpty();
        File.Exists(Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin")).Should().BeTrue();
        (await _envelopeStore.GetAsync($"cert-env-{assetId}")).Should().NotBeNull();
    }

    // --- Skip-canonical-export tests (iOS raw PKCS#12 storage) ---

    [Test]
    public async Task ImportAsync_PfxSkipCanonicalExport_StoresOriginalPassword()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, pfxBytes, null, password,
            SkipCanonicalExport: true);
        var ownerId = Guid.NewGuid();

        var assetId = await _store.ImportAsync(ownerId, request);

        var envelopeJson = await _envelopeStore.GetAsync($"cert-env-{assetId}");
        envelopeJson.Should().NotBeNull();
        var envelope = JsonSerializer.Deserialize<JsonElement>(envelopeJson);
        var storedPassword = envelope.GetProperty("p").GetString();
        storedPassword.Should().Be(password);
    }

    [Test]
    public async Task ImportAsync_PfxSkipCanonicalExport_RoundTrip_ReturnsValidCertWithPrivateKey()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, pfxBytes, null, password,
            SkipCanonicalExport: true);
        var assetId = await _store.ImportAsync(ownerId, request);

        var bundle = await _store.LoadAsync(ownerId, assetId);

        bundle.Should().NotBeNull();
        bundle.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    [Test]
    public async Task ImportAsync_PfxSkipCanonicalExport_StoredCiphertextIsOriginalBytes()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, pfxBytes, null, password,
            SkipCanonicalExport: true);
        var assetId = await _store.ImportAsync(ownerId, request);

        var blobPath = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");
        var blob = await File.ReadAllBytesAsync(blobPath);

        var headerAssetId = Encoding.ASCII.GetString(blob, 0, 36);
        var headerOwner = Encoding.ASCII.GetString(blob, 36, 36);
        var headerVersion = blob[72];

        const int nonceLen = 12;
        const int tagLen = 16;
        var nonce = blob[73..(73 + nonceLen)];
        var ciphertext = blob[(73 + nonceLen)..^tagLen];
        var tag = blob[^tagLen..];

        var envelopeJson = await _envelopeStore.GetAsync($"cert-env-{assetId}");
        envelopeJson.Should().NotBeNull();
        var envelope = JsonSerializer.Deserialize<JsonElement>(envelopeJson);
        var encKey = Convert.FromBase64String(envelope.GetProperty("k").GetString()!);

        var aad = Encoding.UTF8.GetBytes($"{assetId}|{headerAssetId}|{headerOwner}|{headerVersion}");
        var decrypted = new byte[ciphertext.Length];

        using (var aes = new AesGcm(encKey, tagLen))
        {
            aes.Decrypt(nonce, ciphertext, tag, decrypted, aad);
        }

        decrypted.Should().Equal(pfxBytes);
    }

    [Test]
    public async Task ImportAsync_PfxSkipCanonicalExport_WrongPassword_Throws()
    {
        var (pfxBytes, _) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, pfxBytes, null, "wrong",
            SkipCanonicalExport: true);
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*password*incorrect*corrupt*");
    }

    [Test]
    public async Task ImportAsync_PfxSkipCanonicalExport_NoPrivateKey_Throws()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NoKey", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        using var pubOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var pubOnlyPfx = pubOnlyCert.Export(X509ContentType.Pfx, "");
        var request = new CertificateImportRequest(
            CertificateInputMode.Pfx, pubOnlyPfx, null, "",
            SkipCanonicalExport: true);
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*private key*");
    }

    // --- Step 4.3: Additional Import tests ---

    [Test]
    public async Task ImportAsync_PfxWrongPassword_ThrowsCertificateImportException()
    {
        var (pfxBytes, _) = TestCertFactory.CreatePfx();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, "wrong");
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*password*incorrect*corrupt*");
    }

    [Test]
    public async Task ImportAsync_PfxEmptyPassword_PfxCreatedWithEmptyPassword_Succeeds()
    {
        var (pfxBytes, password) = TestCertFactory.CreateEmptyPasswordPfx();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemRsaUnencrypted_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemEcdsaUnencrypted_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemEcdsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemEncryptedRsaCorrectPassword_Succeeds()
    {
        var (certPem, encKeyPem, password) = TestCertFactory.CreatePemEncryptedRsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, encKeyPem, password);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemEncryptedRsaWrongPassword_Throws()
    {
        var (certPem, encKeyPem, _) = TestCertFactory.CreatePemEncryptedRsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, encKeyPem, "wrong");
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>().WithMessage("*password*incorrect*");
    }

    [Test]
    public async Task ImportAsync_PemEncryptedNoPassword_Throws()
    {
        var (certPem, encKeyPem, _) = TestCertFactory.CreatePemEncryptedRsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, encKeyPem, null);
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>().WithMessage("*encrypted*password*");
    }

    [Test]
    public async Task ImportAsync_PemEncryptedEcdsaCorrectPassword_Succeeds()
    {
        var (certPem, encKeyPem, password) = TestCertFactory.CreatePemEncryptedEcdsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, encKeyPem, password);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemEncryptedEcdsaWrongPassword_Throws()
    {
        var (certPem, encKeyPem, _) = TestCertFactory.CreatePemEncryptedEcdsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, encKeyPem, "wrong");
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>().WithMessage("*password*incorrect*");
    }

    [Test]
    public async Task ImportAsync_PemMismatchedKey_Throws()
    {
        var (certPem, _) = TestCertFactory.CreatePemRsa();
        var (_, ecdsaKeyPem) = TestCertFactory.CreatePemEcdsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, certPem, ecdsaKeyPem, null);
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>().WithMessage("*not match*");
    }

    [Test]
    public async Task ImportAsync_PfxNoPrivateKey_Throws()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=NoKey", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
        using var pubOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var pubOnlyPfx = pubOnlyCert.Export(X509ContentType.Pfx, "");
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pubOnlyPfx, null, "");
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>().WithMessage("*private key*");
    }

    // --- Step 4.4: LoadAsync tests ---

    [Test]
    public async Task LoadAsync_RoundTrip_ReturnsValidCertWithPrivateKey()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var bundle = await _store.LoadAsync(ownerId, assetId);

        bundle.Should().NotBeNull();
        bundle.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    [Test]
    public async Task LoadAsync_StoredNonCurrentVersion_UsesStoredVersionInAad()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var path = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");
        var blob = await File.ReadAllBytesAsync(path);
        CertificateAssetBlobCodec.TryParseHeader(blob, out var header).Should().BeTrue();
        CertificateAssetBlobCodec.TrySplit(blob, out var parts).Should().BeTrue();

        var envelopeJson = await _envelopeStore.GetAsync(CertificateAssetBlobCodec.EnvelopeKey(assetId));
        var envelope = JsonSerializer.Deserialize<JsonElement>(envelopeJson!);
        var encryptionKey = Convert.FromBase64String(envelope.GetProperty("k").GetString()!);
        var originalAad = CertificateAssetBlobCodec.BuildAad(
            assetId, header.AssetId, header.Owner, header.Version);
        CertificateAssetBlobCodec.TryDecrypt(encryptionKey, parts, originalAad, out var decrypted)
            .Should().BeTrue();

        const byte storedVersion = 99;
        var storedHeader = CertificateAssetBlobCodec.BuildHeader(assetId, ownerId);
        storedHeader[CertificateAssetBlobCodec.VersionOffset] = storedVersion;
        var storedAad = CertificateAssetBlobCodec.BuildAad(
            assetId, header.AssetId, header.Owner, storedVersion);
        var storedParts = CertificateAssetBlobCodec.Encrypt(encryptionKey, decrypted, storedAad);
        await File.WriteAllBytesAsync(path, CertificateAssetBlobCodec.Assemble(storedHeader, storedParts));

        var bundle = await _store.LoadAsync(ownerId, assetId);

        bundle.Should().NotBeNull();
        bundle.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    [Test]
    public async Task LoadAsync_WrongOwner_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var result = await _store.LoadAsync(Guid.NewGuid(), assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_NonExistentAsset_ReturnsNull()
    {
        var result = await _store.LoadAsync(Guid.NewGuid(), Guid.NewGuid().ToString("D"));
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_TamperedCiphertext_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var path = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");
        var blob = await File.ReadAllBytesAsync(path);
        blob[80] ^= 0xFF;
        await File.WriteAllBytesAsync(path, blob);

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_TamperedHeaderOwner_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var path = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");
        var blob = await File.ReadAllBytesAsync(path);
        blob[40] ^= 0xFF;
        await File.WriteAllBytesAsync(path, blob);

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_MissingEnvelope_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await _envelopeStore.RemoveAsync($"cert-env-{assetId}");

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task ImportAsync_NonceUniquePerWrite()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var id1 = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));
        var id2 = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var blob1 = await File.ReadAllBytesAsync(Path.Combine(_store.CertificatesDirectory, $"cert-{id1}.bin"));
        var blob2 = await File.ReadAllBytesAsync(Path.Combine(_store.CertificatesDirectory, $"cert-{id2}.bin"));

        var nonce1 = blob1[73..85];
        var nonce2 = blob2[73..85];
        nonce1.Should().NotBeEquivalentTo(nonce2);
    }

    // --- Step 4.4b: Corrupt envelope paths ---

    [Test]
    public async Task LoadAsync_MalformedEnvelopeJson_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await _envelopeStore.SetAsync($"cert-env-{assetId}", "not valid json{{");

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_EnvelopeMissingKeyProperty_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await _envelopeStore.SetAsync($"cert-env-{assetId}", """{"v":1,"p":"testpass"}""");

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_EnvelopeMissingPasswordProperty_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await _envelopeStore.SetAsync($"cert-env-{assetId}", """{"v":1,"k":"dGVzdA=="}""");

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_EnvelopeInvalidBase64Key_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await _envelopeStore.SetAsync($"cert-env-{assetId}", """{"v":1,"k":"!!!invalid-base64!!!","p":"test"}""");

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_EnvelopeStoreThrows_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var failingStore = new FailingEnvelopeKeyStore();
        var store = new CertificateAssetStore(failingStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());

        var result = await store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task LoadAsync_InvalidAssetIdFormat_ThrowsCertificateImportException()
    {
        var act = () => _store.LoadAsync(Guid.NewGuid(), "not-a-guid");
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*Invalid certificate asset ID*");
    }

    [Test]
    public async Task DeleteAsync_InvalidAssetIdFormat_ThrowsCertificateImportException()
    {
        var act = () => _store.DeleteAsync(Guid.NewGuid(), "not-a-guid");
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*Invalid certificate asset ID*");
    }

    [Test]
    public async Task PublishAsync_InvalidAssetIdFormat_ThrowsCertificateImportException()
    {
        var act = () => _store.PublishAsync("not-a-guid", "/tmp/fake");
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*Invalid certificate asset ID*");
    }

    [Test]
    public async Task LoadAsync_PemImportAfterStoreReconstruction_ReturnsValidCertWithPrivateKey()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pem, certPem, keyPem, null));

        // Simulate app restart by creating a fresh CertificateAssetStore with the same backing store
        var freshStore = new CertificateAssetStore(_envelopeStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());

        var bundle = await freshStore.LoadAsync(ownerId, assetId);

        bundle.Should().NotBeNull();
        bundle.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    // --- Step 4.5: DeleteAsync tests ---

    [Test]
    public async Task DeleteAsync_ValidAsset_RemovesBlobAndSecret()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await _store.DeleteAsync(ownerId, assetId);

        File.Exists(Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin")).Should().BeFalse();
        (await _envelopeStore.GetAsync($"cert-env-{assetId}")).Should().BeNull();
    }

    [Test]
    public async Task DeleteAsync_WrongOwner_DoesNotDelete()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await _store.DeleteAsync(Guid.NewGuid(), assetId);

        File.Exists(Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin")).Should().BeTrue();
    }

    [Test]
    public async Task DeleteAsync_TamperedBlob_DoesNotDelete()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var path = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");
        var blob = await File.ReadAllBytesAsync(path);
        blob[80] ^= 0xFF;
        await File.WriteAllBytesAsync(path, blob);

        await _store.DeleteAsync(ownerId, assetId);

        File.Exists(path).Should().BeTrue();
    }

    [Test]
    public async Task DeleteAsync_NonExistentAsset_DoesNotThrow()
    {
        var act = () => _store.DeleteAsync(Guid.NewGuid(), Guid.NewGuid().ToString("D"));
        await act.Should().NotThrowAsync();
    }

    // --- Step 4.6: ListAssetsAsync tests ---

    [Test]
    public async Task ListAssetsAsync_ReturnsVerifiedPairs()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var assets = await _store.ListAssetsAsync();

        assets.Should().Contain((ownerId, assetId));
    }

    [Test]
    public async Task ListAssetsAsync_ExcludesTmpFiles()
    {
        var tmpPath = Path.Combine(_store.CertificatesDirectory, "cert-fake.bin.tmp");
        await File.WriteAllBytesAsync(tmpPath, new byte[100]);

        var assets = await _store.ListAssetsAsync();
        assets.Should().BeEmpty();
    }

    [Test]
    public async Task ListAssetsAsync_ExcludesQuarantineFiles()
    {
        var qPath = Path.Combine(_store.CertificatesDirectory, "cert-fake.quarantine");
        await File.WriteAllBytesAsync(qPath, new byte[100]);

        var assets = await _store.ListAssetsAsync();
        assets.Should().BeEmpty();
    }

    // --- Step 4.7: Envelope store failure cleanup ---

    [Test]
    public async Task ImportAsync_EnvelopeStoreFailure_CleansUpTempFile()
    {
        var failingStore = new FailingEnvelopeKeyStore();
        var store = new CertificateAssetStore(failingStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());
        var (pfxBytes, password) = TestCertFactory.CreatePfx();

        var act = () => store.ImportAsync(Guid.NewGuid(),
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));
        await act.Should().ThrowAsync<IOException>();

        Directory.EnumerateFiles(store.CertificatesDirectory, "*.tmp").Should().BeEmpty();
        Directory.EnumerateFiles(store.CertificatesDirectory, "*.bin").Should().BeEmpty();
    }

    // --- Step 4.8: Security tests ---

    [Test]
    public async Task ListAssetsAsync_SwappedFilename_SkipsTamperedFile()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var realPath = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");

        var fakeId = Guid.NewGuid().ToString("D");
        var fakePath = Path.Combine(_store.CertificatesDirectory, $"cert-{fakeId}.bin");
        File.Move(realPath, fakePath);

        var assets = await _store.ListAssetsAsync();
        assets.Should().NotContain(a => a.AssetId == assetId);
        assets.Should().NotContain(a => a.AssetId == fakeId);

        File.Delete(fakePath);
    }

    [Test]
    public async Task LoadAsync_TruncatedBlob_ReturnsNull()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var path = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");
        await File.WriteAllBytesAsync(path, new byte[20]);

        var result = await _store.LoadAsync(ownerId, assetId);
        result.Should().BeNull();
    }

    [Test]
    public async Task DeleteAsync_BlobDeleteFails_EnvelopePreserved()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await _store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var path = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");

        using var lockHandle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

        await _store.DeleteAsync(ownerId, assetId);

        File.Exists(path).Should().BeTrue();
        (await _envelopeStore.GetAsync($"cert-env-{assetId}")).Should().NotBeNull();

        lockHandle.Close();
    }

    [Test]
    public async Task PublishAsync_Fails_CleansUpTempAndEnvelope()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var (assetId, tempPath) = await _store.ImportStagedAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        File.Delete(tempPath);

        var act = () => _store.PublishAsync(assetId, tempPath);
        await act.Should().ThrowAsync<CertificateImportException>();

        (await _envelopeStore.GetAsync($"cert-env-{assetId}")).Should().BeNull();
    }

    [Test]
    public async Task ImportAsync_PemDerCertificate_Succeeds()
    {
        using var cert = TestCertFactory.CreateRsaCert();
        var derBytes = cert.Export(X509ContentType.Cert);
        var keyPem = Encoding.UTF8.GetBytes(cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        var request = new CertificateImportRequest(CertificateInputMode.Pem, derBytes, keyPem, null);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemWithUtf8Bom_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var bomCertPem = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(certPem).ToArray();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, bomCertPem, keyPem, null);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemCertSlotIsPrivateKey_ThrowsHelpful()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, keyPem, certPem, null);
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*looks like a private key*");
    }

    [Test]
    public async Task ImportAsync_PemTrustedCertificateLabel_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var certText = Encoding.UTF8.GetString(certPem);
        var trustedCertPem = Encoding.UTF8.GetBytes(
            certText.Replace("BEGIN CERTIFICATE", "BEGIN TRUSTED CERTIFICATE")
                    .Replace("END CERTIFICATE", "END TRUSTED CERTIFICATE"));
        var request = new CertificateImportRequest(CertificateInputMode.Pem, trustedCertPem, keyPem, null);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_PemTextMustNotBeParsedAsDer()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var certText = Encoding.UTF8.GetString(certPem);
        var garbagePem = Encoding.UTF8.GetBytes(
            certText.Replace("BEGIN CERTIFICATE", "BEGIN GARBAGE")
                    .Replace("END CERTIFICATE", "END GARBAGE"));
        var request = new CertificateImportRequest(CertificateInputMode.Pem, garbagePem, keyPem, null);
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*PEM*CERTIFICATE*");
    }

    [Test]
    public async Task ImportAsync_DerPrivateKeyAsCert_ThrowsHelpful()
    {
        using var cert = TestCertFactory.CreateRsaCert();
        var derKeyBytes = cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKey();
        var keyPem = Encoding.UTF8.GetBytes(cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
        var request = new CertificateImportRequest(CertificateInputMode.Pem, derKeyBytes, keyPem, null);
        var act = () => _store.ImportAsync(Guid.NewGuid(), request);
        await act.Should().ThrowAsync<CertificateImportException>()
            .WithMessage("*private key*");
    }

    [Test]
    public async Task ImportAsync_Utf16LePem_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var certText = Encoding.UTF8.GetString(certPem);
        var utf16LeCertPem = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(certText))
            .ToArray();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, utf16LeCertPem, keyPem, null);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_CombinedPemInCertField_Succeeds()
    {
        var (certPem, keyPem) = TestCertFactory.CreatePemRsa();
        var combinedPem = certPem.Concat(keyPem).ToArray();
        var request = new CertificateImportRequest(CertificateInputMode.Pem, combinedPem, keyPem, null);
        var assetId = await _store.ImportAsync(Guid.NewGuid(), request);
        assetId.Should().NotBeNullOrEmpty();
    }

    // --- DuplicateAsync tests ---

    [Test]
    public async Task DuplicateAsync_RoundTrip_Succeeds()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var sourceAssetId = await _store.ImportAsync(sourceOwnerId, request);

        var targetOwnerId = Guid.NewGuid();
        var newAssetId = await _store.DuplicateAsync(sourceOwnerId, sourceAssetId, targetOwnerId);

        newAssetId.Should().NotBeNull();
        newAssetId.Should().NotBe(sourceAssetId);

        var loaded = await _store.LoadAsync(targetOwnerId, newAssetId!);
        loaded.Should().NotBeNull();
        loaded!.Certificate.HasPrivateKey.Should().BeTrue();
        loaded.Certificate.Dispose();
    }

    [Test]
    public async Task DuplicateAsync_PreservesSourceAsset()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var sourceAssetId = await _store.ImportAsync(sourceOwnerId, request);

        var targetOwnerId = Guid.NewGuid();
        await _store.DuplicateAsync(sourceOwnerId, sourceAssetId, targetOwnerId);

        var sourceLoaded = await _store.LoadAsync(sourceOwnerId, sourceAssetId);
        sourceLoaded.Should().NotBeNull("source asset must be preserved after duplication");
        sourceLoaded!.Certificate.Dispose();
    }

    [Test]
    public async Task DuplicateAsync_TargetOwnerEnforced()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var sourceAssetId = await _store.ImportAsync(sourceOwnerId, request);

        var targetOwnerId = Guid.NewGuid();
        var newAssetId = await _store.DuplicateAsync(sourceOwnerId, sourceAssetId, targetOwnerId);

        // Target owner can load
        var loaded = await _store.LoadAsync(targetOwnerId, newAssetId!);
        loaded.Should().NotBeNull();
        loaded!.Certificate.Dispose();

        // Source owner cannot load the duplicate
        var sourceLoad = await _store.LoadAsync(sourceOwnerId, newAssetId!);
        sourceLoad.Should().BeNull("duplicate must be owned by target, not source");
    }

    [Test]
    public async Task DuplicateAsync_UniqueKeyAndCiphertext()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var sourceAssetId = await _store.ImportAsync(sourceOwnerId, request);

        var targetOwnerId = Guid.NewGuid();
        var newAssetId = await _store.DuplicateAsync(sourceOwnerId, sourceAssetId, targetOwnerId);

        var sourcePath = Path.Combine(_store.CertificatesDirectory, $"cert-{sourceAssetId}.bin");
        var targetPath = Path.Combine(_store.CertificatesDirectory, $"cert-{newAssetId}.bin");
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var targetBytes = await File.ReadAllBytesAsync(targetPath);

        sourceBytes.Should().NotBeEquivalentTo(targetBytes,
            "duplicate must have different ciphertext (different encryption key)");
    }

    [Test]
    public async Task DuplicateAsync_ReturnsNull_ForMissingSource()
    {
        var result = await _store.DuplicateAsync(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid());
        result.Should().BeNull();
    }

    [Test]
    public async Task DuplicateAsync_ReturnsNull_ForWrongSourceOwner()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var sourceAssetId = await _store.ImportAsync(sourceOwnerId, request);

        var wrongOwnerId = Guid.NewGuid();
        var result = await _store.DuplicateAsync(wrongOwnerId, sourceAssetId, Guid.NewGuid());
        result.Should().BeNull("wrong source owner must fail");
    }

    [Test]
    public async Task DuplicateAsync_ReturnsNull_ForTamperedEnvelope()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var sourceAssetId = await _store.ImportAsync(sourceOwnerId, request);

        // Tamper with the envelope
        await _envelopeStore.SetAsync($"cert-env-{sourceAssetId}", "invalid-json");

        var result = await _store.DuplicateAsync(sourceOwnerId, sourceAssetId, Guid.NewGuid());
        result.Should().BeNull("tampered envelope must fail duplication");
    }

    [Test]
    public async Task DuplicateAsync_RollsBack_WhenEnvelopeWriteFails()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var request = new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password);
        var sourceAssetId = await _store.ImportAsync(sourceOwnerId, request);

        // Verify source is loadable before testing rollback
        var sourceLoaded = await _store.LoadAsync(sourceOwnerId, sourceAssetId);
        sourceLoaded.Should().NotBeNull("source must be loadable before rollback test");
        sourceLoaded!.Certificate.Dispose();

        // Create a failing store that can read source envelopes but fails on target SetAsync
        var sourceEnvelopeJson = await _envelopeStore.GetAsync($"cert-env-{sourceAssetId}");
        sourceEnvelopeJson.Should().NotBeNull("source envelope must exist");
        var failingEnvelopeStore = new DuplicateFailingEnvelopeKeyStore(sourceAssetId, sourceEnvelopeJson!);
        var failingStore = new CertificateAssetStore(failingEnvelopeStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());

        var result = await failingStore.DuplicateAsync(sourceOwnerId, sourceAssetId, Guid.NewGuid());
        result.Should().BeNull("envelope write failure must cause rollback");

        // No orphaned blob should remain (only the source .bin)
        var certFiles = Directory.GetFiles(_store.CertificatesDirectory, "cert-*.bin");
        certFiles.Should().HaveCount(1, "only the source blob should remain after rollback");

        // No orphaned temp file should remain
        var tempFiles = Directory.GetFiles(_store.CertificatesDirectory, "cert-*.bin.tmp");
        tempFiles.Should().BeEmpty("temp file must be removed after rollback");

        // Verify the target envelope is absent after rollback (RemoveAsync cleaned it up)
        failingEnvelopeStore.GetAsync(failingEnvelopeStore.LastSetKey!).Result.Should().BeNull(
            "the exact target envelope key must be absent after rollback");

        // Verify SetAsync was called exactly once (for the target) and threw after write
        failingEnvelopeStore.SetAsyncCallCount.Should().Be(1,
            "SetAsync should have been called exactly once (for the target) and thrown after write");
        failingEnvelopeStore.LastSetKey.Should().StartWith("cert-env-",
            "SetAsync key must be a target envelope key");

        // Verify RemoveAsync was called with the exact same target envelope key during rollback
        failingEnvelopeStore.RemoveAsyncCallCount.Should().BeGreaterThanOrEqualTo(1,
            "rollback must call RemoveAsync on the target envelope key");
        failingEnvelopeStore.LastRemovedKey.Should().Be(failingEnvelopeStore.LastSetKey,
            "RemoveAsync must target the exact same key that SetAsync wrote before throwing");

        // Verify the target envelope key is absent from the store (rollback removed the write-then-threw data)
        failingEnvelopeStore.Contains(failingEnvelopeStore.LastSetKey!).Should().BeFalse(
            "the target envelope key must not exist in the store after rollback");

        // Source must still be loadable after rollback
        var sourceAfter = await _store.LoadAsync(sourceOwnerId, sourceAssetId);
        sourceAfter.Should().NotBeNull("source must survive rollback");
        sourceAfter!.Certificate.Dispose();
    }

    private sealed class DuplicateFailingEnvelopeKeyStore : ICertificateEnvelopeKeyStore
    {
        private readonly string _sourceAssetId;
        private readonly string _sourceEnvelopeJson;
        private readonly Dictionary<string, string> _store = new();

        public int SetAsyncCallCount { get; private set; }
        public string? LastSetKey { get; private set; }
        public int RemoveAsyncCallCount { get; private set; }
        public string? LastRemovedKey { get; private set; }

        public DuplicateFailingEnvelopeKeyStore(string sourceAssetId, string sourceEnvelopeJson)
        {
            _sourceAssetId = sourceAssetId;
            _sourceEnvelopeJson = sourceEnvelopeJson;
        }

        public Task<string?> GetAsync(string key)
        {
            // Allow reading the source envelope so decryption succeeds
            if (key == $"cert-env-{_sourceAssetId}")
                return Task.FromResult<string?>(_sourceEnvelopeJson);
            // Also return stored values so rollback can be verified as having completed
            return Task.FromResult(_store.GetValueOrDefault(key));
        }

        public Task SetAsync(string key, string value)
        {
            SetAsyncCallCount++;
            LastSetKey = key;
            // Model write-then-throw: persist the data, then throw to trigger rollback.
            // This proves the rollback path calls RemoveAsync on the exact target key,
            // and that RemoveAsync actually removes what was written.
            _store[key] = value;
            throw new IOException("envelope write failed");
        }

        public Task RemoveAsync(string key)
        {
            RemoveAsyncCallCount++;
            LastRemovedKey = key;
            _store.Remove(key);
            return Task.CompletedTask;
        }

        public bool Contains(string key) => _store.ContainsKey(key);
    }
}
