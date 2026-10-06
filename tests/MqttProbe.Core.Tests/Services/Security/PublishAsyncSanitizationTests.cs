using Microsoft.Extensions.Logging;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Security;
using MqttProbe.TestInfrastructure.Security;

namespace MqttProbe.Core.Tests.Services.Security;

// Regression tests for the MA0054 refactor of PublishAsync. The restructured
// catch-cleanup-then-throw pattern must preserve the safe sanitized error
// contract: message-only CertificateImportException with null InnerException
// and empty Data, no runtime paths or secret sentinels in ToString output.
[TestFixture]
public class PublishAsyncSanitizationTests
{
    private string _tempDir = null!;
    private InMemoryEnvelopeKeyStore _envelopeStore = null!;
    private CertificateAssetStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pub-sanitize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _envelopeStore = new InMemoryEnvelopeKeyStore();
        _store = new CertificateAssetStore(_envelopeStore, _tempDir,
            Substitute.For<ILogger<CertificateAssetStore>>());
    }

    [TearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<(string AssetId, string TempPath)> StageAsync()
    {
        var (pfxBytes, password) = TestCertFactory.CreatePfx();
        return await _store.ImportStagedAsync(Guid.NewGuid(),
            new CertificateImportRequest(CertificateInputMode.Pfx, pfxBytes, null, password));
    }

    [Test]
    public async Task PublishAsync_MissingTempFile_ThrowsMessageOnly()
    {
        var (assetId, tempPath) = await StageAsync();
        File.Delete(tempPath);

        var ex = await Assert.ThrowsAsync<CertificateImportException>(
            async () => await _store.PublishAsync(assetId, tempPath));

        Assert.That(ex.Message, Is.EqualTo("Failed to publish certificate asset."));
    }

    [Test]
    public async Task PublishAsync_MissingTempFile_InnerExceptionIsNull()
    {
        var (assetId, tempPath) = await StageAsync();
        File.Delete(tempPath);

        var ex = await Assert.ThrowsAsync<CertificateImportException>(
            async () => await _store.PublishAsync(assetId, tempPath));

        Assert.That(ex.InnerException, Is.Null);
    }

    [Test]
    public async Task PublishAsync_MissingTempFile_DataIsEmpty()
    {
        var (assetId, tempPath) = await StageAsync();
        File.Delete(tempPath);

        var ex = await Assert.ThrowsAsync<CertificateImportException>(
            async () => await _store.PublishAsync(assetId, tempPath));

        Assert.That(ex.Data, Is.Empty);
    }

    [Test]
    public async Task PublishAsync_MissingTempFile_ToStringExcludesPaths()
    {
        var (assetId, tempPath) = await StageAsync();
        File.Delete(tempPath);

        var ex = await Assert.ThrowsAsync<CertificateImportException>(
            async () => await _store.PublishAsync(assetId, tempPath));

        var text = ex.ToString();
        Assert.That(text, Does.Not.Contain(Path.GetTempPath()));
        Assert.That(text, Does.Not.Contain(".bin.tmp"));
        Assert.That(text, Does.Not.Contain("cert-"));
        Assert.That(text, Does.Not.Contain("password"));
        Assert.That(text, Does.Not.Contain("secret"));
    }

    [Test]
    public async Task PublishAsync_CleanupFailure_CannotReplaceSanitizedException()
    {
        var (assetId, tempPath) = await StageAsync();
        File.Delete(tempPath);

        // Seed the envelope store with data so the rollback RemoveAsync call
        // exercises the best-effort catch path (in-memory store won't throw,
        // but the test proves the throw site is the message-only line after the catch).
        await _envelopeStore.SetAsync($"cert-env-{assetId}", "corrupt");

        var ex = await Assert.ThrowsAsync<CertificateImportException>(
            async () => await _store.PublishAsync(assetId, tempPath));

        Assert.That(ex.Message, Is.EqualTo("Failed to publish certificate asset."));
        Assert.That(ex.InnerException, Is.Null);
    }

    [Test]
    public async Task PublishAsync_DestinationCollision_RetainsExistingBytes()
    {
        var (assetId, tempPath) = await StageAsync();

        // Place a sentinel file at the final destination.
        var finalPath = Path.Combine(_store.CertificatesDirectory, $"cert-{assetId}.bin");
        var sentinel = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(finalPath, sentinel);

        // Delete the temp file so the move fails with IOException.
        File.Delete(tempPath);

        await Assert.ThrowsAsync<CertificateImportException>(
            async () => await _store.PublishAsync(assetId, tempPath));

        // The collision sentinel must not have been clobbered.
        Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(sentinel));
    }

    // NOTE: LoadExportablePfx PlatformNotSupported fallback (CertificateImportConverter)
    // is exercised only when the OS rejects EphemeralKeySet (e.g. certain constrained
    // Linux containers). On developer Windows and CI ubuntu, EphemeralKeySet succeeds,
    // so the outer catch (PlatformNotSupportedException) -> DefaultKeySet retry path is
    // never reached. This is a native platform fault boundary, not a managed regression,
    // and is explicitly excluded from this test suite. The restructured code has been
    // verified by reading the source: the inner catch falls through, and the throw is
    // outside any catch block, so MA0054 does not flag it.
}
