using System.Text.Json;
using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;

namespace MqttProbe.Core.Tests.Services.Security;

[TestFixture]
public class MauiCertificateAssetStoreTests
{
    private string _tempDir = null!;
    private InMemoryEnvelopeKeyStore _envelopeStore = null!;
    private CertificateAssetStore _baseStore = null!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"maui-cert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _envelopeStore = new InMemoryEnvelopeKeyStore();
        _baseStore = new CertificateAssetStore(_envelopeStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());
    }

    [TearDown]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public async Task ImportAsync_ProtectionsAppliedBeforePublish_AssetLoadable()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        assetId.Should().NotBeNullOrEmpty();
        protector.ProtectionCallCount.Should().Be(1);

        var bundle = await store.LoadAsync(ownerId, assetId);
        bundle.Should().NotBeNull();
        bundle!.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    [Test]
    public async Task ImportAsync_ProtectionFails_CleansUpTempAndEnvelope_Throws()
    {
        var protector = new StubFileProtector(applySucceeds: false);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var act = () => store.ImportAsync(Guid.NewGuid(),
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));
        await act.Should().ThrowAsync<CertificateImportException>();

        Directory.EnumerateFiles(store.CertificatesDirectory, "*.bin").Should().BeEmpty();
        Directory.EnumerateFiles(store.CertificatesDirectory, "*.tmp").Should().BeEmpty();
    }

    [Test]
    public async Task ImportAsync_ProtectionFails_DeleteAlsoFails_Quarantines()
    {
        var protector = new StubFileProtector(applySucceeds: false, deleteSucceeds: false, moveSucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var act = () => store.ImportAsync(Guid.NewGuid(),
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));
        await act.Should().ThrowAsync<CertificateImportException>();

        Directory.EnumerateFiles(store.CertificatesDirectory, "*.quarantine").Should().NotBeEmpty();
    }

    [Test]
    public async Task ImportAsync_ProtectionAndDeleteAndMoveAllFail_WritesCleanupRetryMarker()
    {
        var protector = new StubFileProtector(applySucceeds: false, deleteSucceeds: false, moveSucceeds: false);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var act = () => store.ImportAsync(Guid.NewGuid(),
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));
        await act.Should().ThrowAsync<CertificateImportException>();

        Directory.EnumerateFiles(store.CertificatesDirectory, "*.cleanup-retry").Should().NotBeEmpty();
    }

    [Test]
    public async Task ImportAsync_ProtectionFails_EnvelopeSecretRemoved()
    {
        var protector = new StubFileProtector(applySucceeds: false);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var act = () => store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));
        await act.Should().ThrowAsync<CertificateImportException>();

        _envelopeStore.IsEmpty.Should().BeTrue();
    }

    [Test]
    public async Task LoadAsync_DelegatesToBaseStore()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var bundle = await store.LoadAsync(ownerId, assetId);
        bundle.Should().NotBeNull();
        bundle!.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    [Test]
    public async Task DeleteAsync_DelegatesToBaseStore()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        await store.DeleteAsync(ownerId, assetId);

        File.Exists(Path.Combine(_baseStore.CertificatesDirectory, $"cert-{assetId}.bin")).Should().BeFalse();
    }

    [Test]
    public async Task ListAssetsAsync_DelegatesToBaseStore()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var assets = await store.ListAssetsAsync();
        assets.Should().Contain((ownerId, assetId));
    }

    [Test]
    public void CertificatesDirectory_ReturnsBaseStoreDirectory()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        store.CertificatesDirectory.Should().Be(_baseStore.CertificatesDirectory);
    }

    [Test]
    public async Task ImportAsync_StoresOriginalPassword_NotRandomGuid()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var ownerId = Guid.NewGuid();
        var assetId = await store.ImportAsync(ownerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        var envelopeJson = await _envelopeStore.GetAsync($"cert-env-{assetId}");
        envelopeJson.Should().NotBeNull();
        var envelope = JsonSerializer.Deserialize<JsonElement>(envelopeJson!);
        var storedPassword = envelope.GetProperty("p").GetString();

        storedPassword.Should().Be(password);

        Guid.TryParse(storedPassword, out _).Should().BeFalse(
            "stored password should be the original, not a random GUID");

        var bundle = await store.LoadAsync(ownerId, assetId);
        bundle.Should().NotBeNull();
        bundle!.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    // --- DuplicateAsync MAUI tests ---

    [Test]
    public async Task DuplicateAsync_ProtectionAppliedToTempPath_BeforePublish()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var store = new MauiCertificateAssetStore(
            _baseStore, _baseStore, _envelopeStore,
            _baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var sourceAssetId = await store.ImportAsync(sourceOwnerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        protector.ProtectionCallCount.Should().Be(1, "import should call protection");

        var targetOwnerId = Guid.NewGuid();
        var newAssetId = await store.DuplicateAsync(sourceOwnerId, sourceAssetId, targetOwnerId);

        newAssetId.Should().NotBeNull();
        protector.ProtectionCallCount.Should().Be(2, "duplicate should also call protection");

        // Verify protector received a .tmp path (staged, not final)
        protector.LastProtectedPath.Should().NotBeNull();
        protector.LastProtectedPath.Should().EndWith(".tmp",
            "protector must receive temp path before publish, not the final .bin path");

        // Verify final published blob exists (not the temp)
        var finalPath = Path.Combine(_baseStore.CertificatesDirectory, $"cert-{newAssetId}.bin");
        File.Exists(finalPath).Should().BeTrue("final published blob must exist");
        var tempPath = Path.Combine(_baseStore.CertificatesDirectory, $"cert-{newAssetId}.bin.tmp");
        File.Exists(tempPath).Should().BeFalse("temp path must be gone after publish");

        var bundle = await store.LoadAsync(targetOwnerId, newAssetId!);
        bundle.Should().NotBeNull();
        bundle!.Certificate.HasPrivateKey.Should().BeTrue();
        bundle.Certificate.Dispose();
    }

    [Test]
    public async Task DuplicateAsync_ProtectionFails_NoPublishedBlobOrEnvelope()
    {
        var protector = new StubFileProtector(applySucceeds: true);
        var envelopeStore = new InMemoryEnvelopeKeyStore();
        var baseStore = new CertificateAssetStore(envelopeStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());
        var store = new MauiCertificateAssetStore(
            baseStore, baseStore, envelopeStore,
            baseStore.CertificatesDirectory, protector,
            Substitute.For<ILogger<MauiCertificateAssetStore>>());

        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        var sourceOwnerId = Guid.NewGuid();
        var sourceAssetId = await store.ImportAsync(sourceOwnerId,
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));

        // Verify source envelope exists before duplicate attempt
        var sourceEnvelopeBefore = await envelopeStore.GetAsync($"cert-env-{sourceAssetId}");
        sourceEnvelopeBefore.Should().NotBeNull("source envelope must exist before duplicate");

        // Now make protection fail for the duplicate
        protector.SetNextApplySucceeds(false);

        var targetOwnerId = Guid.NewGuid();
        var result = await store.DuplicateAsync(sourceOwnerId, sourceAssetId, targetOwnerId);

        result.Should().BeNull("protection failure must cause abort");

        // No published .bin blob for the target should exist (only source)
        var certFiles = Directory.GetFiles(baseStore.CertificatesDirectory, "cert-*.bin");
        certFiles.Should().HaveCount(1, "only source blob should exist after protection failure");

        // No temp file should remain
        var tempFiles = Directory.GetFiles(baseStore.CertificatesDirectory, "cert-*.bin.tmp");
        tempFiles.Should().BeEmpty("temp file must be cleaned up after protection failure");

        // Source envelope must still exist
        var sourceEnvelopeAfter = await envelopeStore.GetAsync($"cert-env-{sourceAssetId}");
        sourceEnvelopeAfter.Should().NotBeNull("source envelope must survive protection failure");

        // Target envelope must NOT exist (rollback removed it or it was never written)
        // We can verify by checking that only the source envelope key exists
        var allKeys = envelopeStore.GetAllKeys();
        allKeys.Should().Contain($"cert-env-{sourceAssetId}",
            "source envelope key must exist");
        allKeys.Should().HaveCount(1,
            "only source envelope key should exist; target envelope must be absent");

        // Source should still be intact
        var sourceBundle = await store.LoadAsync(sourceOwnerId, sourceAssetId);
        sourceBundle.Should().NotBeNull("source must be preserved after failed duplicate");
        sourceBundle!.Certificate.Dispose();
    }
}

internal class StubFileProtector : IFileProtector
{
    private readonly bool _applySucceeds;
    private readonly bool _deleteSucceeds;
    private readonly bool _moveSucceeds;
    private bool? _nextApplySucceeds;
    public int ProtectionCallCount { get; private set; }
    public string? LastProtectedPath { get; private set; }

    public StubFileProtector(bool applySucceeds, bool deleteSucceeds = true, bool moveSucceeds = true)
    {
        _applySucceeds = applySucceeds;
        _deleteSucceeds = deleteSucceeds;
        _moveSucceeds = moveSucceeds;
    }

    public void SetNextApplySucceeds(bool succeeds) => _nextApplySucceeds = succeeds;

    public bool ApplyProtections(string path)
    {
        ProtectionCallCount++;
        LastProtectedPath = path;
        if (_nextApplySucceeds is { } next)
        {
            _nextApplySucceeds = null;
            return next;
        }
        return _applySucceeds;
    }

    public bool TryDelete(string path)
    {
        if (!_deleteSucceeds) return false;
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    public bool TryMoveToQuarantine(string sourcePath, string quarantinePath)
    {
        if (!_moveSucceeds) return false;
        try { File.Move(sourcePath, quarantinePath); return true; }
        catch { return false; }
    }
}
