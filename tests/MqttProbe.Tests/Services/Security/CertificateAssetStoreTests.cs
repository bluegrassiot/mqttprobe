using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Tests.Services.Security.TestHelpers;

namespace MqttProbe.Tests.Services.Security;

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
        var envelope = JsonSerializer.Deserialize<JsonElement>(envelopeJson!);
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
        bundle!.Certificate.HasPrivateKey.Should().BeTrue();
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
        var envelope = JsonSerializer.Deserialize<JsonElement>(envelopeJson!);
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
        bundle!.Certificate.HasPrivateKey.Should().BeTrue();
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
        bundle!.Certificate.HasPrivateKey.Should().BeTrue();
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
}
