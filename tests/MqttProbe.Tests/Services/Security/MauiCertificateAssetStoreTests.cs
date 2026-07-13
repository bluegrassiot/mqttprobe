using Microsoft.Extensions.Logging;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;
using MqttProbe.Tests.Services.Security.TestHelpers;

namespace MqttProbe.Tests.Services.Security;

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
}

internal class StubFileProtector : IFileProtector
{
    private readonly bool _applySucceeds;
    private readonly bool _deleteSucceeds;
    private readonly bool _moveSucceeds;
    public int ProtectionCallCount { get; private set; }

    public StubFileProtector(bool applySucceeds, bool deleteSucceeds = true, bool moveSucceeds = true)
    {
        _applySucceeds = applySucceeds;
        _deleteSucceeds = deleteSucceeds;
        _moveSucceeds = moveSucceeds;
    }

    public bool ApplyProtections(string path)
    {
        ProtectionCallCount++;
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
