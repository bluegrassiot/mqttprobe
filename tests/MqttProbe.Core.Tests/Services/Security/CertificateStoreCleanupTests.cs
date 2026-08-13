using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Core.Models.Mqtt;
using MqttProbe.Core.Services.Security;

namespace MqttProbe.Core.Tests.Services.Security;

// Three branches are deliberately uncovered here. Do not try to force any of them:
//   1. QuarantineStagingFileAsync's fallback (the .cleanup-retry marker + LogCritical path,
//      used when a .bin.tmp cannot even be deleted or renamed) needs a file the process
//      genuinely cannot delete. A test that holds a FileStream open and expects File.Delete
//      to fail would be flaky rather than portable — it fails on Windows but succeeds on Linux.
//   2. The false branch of the File.Exists(correspondingTmp) guard in
//      DeleteCleanupRetryMarkersAsync cannot be reached through RunAsync. Stage 1 always
//      deletes a .bin.tmp together with its .cleanup-retry marker, so a marker with a
//      surviving temp means stage 1 already failed — the quarantine path above. Keep the
//      guard; it is correct defensive code.
//   3. The .tmp/.quarantine/.cleanup-retry suffix guard in SweepUnverifiedBlobsAsync.
//      Reaching it needs a staging temp that survived stage 1, i.e. the quarantine path
//      above. The guard exists because Windows matches "cert-x.bin.tmp" against the
//      "cert-*.bin" glob via legacy 8.3 short names.
[TestFixture]
public class CertificateStoreCleanupTests
{
    private string _certDir = null!;
    private FakeAssetStore _certStore = null!;
    private FakeEnvelopeKeyStore _envelopeKeys = null!;
    private FakeTimeProvider _clock = null!;
    private CertificateStoreCleanup _sut = null!;

    [SetUp]
    public void Setup()
    {
        _certDir = Path.Combine(Path.GetTempPath(), $"mqttprobe_certs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_certDir);
        _certStore = new FakeAssetStore(_certDir);
        _envelopeKeys = new FakeEnvelopeKeyStore();
        // Started at the real clock, not FakeTimeProvider's 2000-01-01 default: ageing compares
        // the injected clock against real File.GetCreationTime timestamps.
        _clock = new FakeTimeProvider(DateTimeOffset.Now);
        _sut = new CertificateStoreCleanup(
            _certStore, _envelopeKeys, NullLogger<CertificateStoreCleanup>.Instance, _clock);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_certDir)) Directory.Delete(_certDir, recursive: true);
    }

    // --- staging temps ---

    [Test]
    public async Task RunAsync_DeletesStagingTempAndItsEnvelopeKey()
    {
        var assetId = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(_certDir, $"cert-{assetId}.bin.tmp"), "partial");

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(Path.Combine(_certDir, $"cert-{assetId}.bin.tmp")).Should().BeFalse();
        _envelopeKeys.Removed.Should().Contain($"cert-env-{assetId}");
    }

    [Test]
    public async Task RunAsync_DeletesRetryMarkerLeftBesideAStagingTemp()
    {
        var assetId = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(_certDir, $"cert-{assetId}.bin.tmp"), "partial");
        await File.WriteAllTextAsync(Path.Combine(_certDir, $"cert-{assetId}.cleanup-retry"), "marker");

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(Path.Combine(_certDir, $"cert-{assetId}.cleanup-retry")).Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_DeletesOrphanedRetryMarkerWhoseTempIsAlreadyGone()
    {
        var assetId = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(_certDir, $"cert-{assetId}.cleanup-retry"), "marker");

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(Path.Combine(_certDir, $"cert-{assetId}.cleanup-retry")).Should().BeFalse();
        _envelopeKeys.Removed.Should().Contain($"cert-env-{assetId}");
    }

    // --- quarantine ageing ---

    // Age is driven by moving the clock forward, never by back-dating the file.
    // File.SetCreationTime does not portably set birth time on Linux, and CI runs on
    // ubuntu-latest, so a back-dated file would age correctly on Windows only.
    [Test]
    public async Task RunAsync_DeletesQuarantineFileOlderThanOneHour()
    {
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(_certDir, $"cert-{assetId}.quarantine");
        await File.WriteAllTextAsync(path, "quarantined");
        _clock.Advance(TimeSpan.FromHours(2));

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeFalse();
        _envelopeKeys.Removed.Should().Contain($"cert-env-{assetId}");
    }

    [Test]
    public async Task RunAsync_KeepsQuarantineFileYoungerThanOneHour()
    {
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(_certDir, $"cert-{assetId}.quarantine");
        await File.WriteAllTextAsync(path, "quarantined");
        _clock.Advance(TimeSpan.FromMinutes(5));

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeTrue();
    }

    // --- the configLoadedSuccessfully gate ---

    [Test]
    public async Task RunAsync_ConfigNotLoaded_SkipsOrphanSweepButStillCleansStaging()
    {
        var orphanId = Guid.NewGuid().ToString("D");
        _certStore.Assets.Add((Guid.NewGuid(), orphanId));
        var tmpId = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(_certDir, $"cert-{tmpId}.bin.tmp"), "partial");

        await _sut.RunAsync([], configLoadedSuccessfully: false);

        File.Exists(Path.Combine(_certDir, $"cert-{tmpId}.bin.tmp")).Should().BeFalse();
        _certStore.Deleted.Should().BeEmpty();
    }

    [Test]
    public async Task RunAsync_ConfigNotLoaded_DoesNotCallListAssets()
    {
        await _sut.RunAsync([], configLoadedSuccessfully: false);

        _certStore.ListAssetsCallCount.Should().Be(0);
    }

    // --- orphan sweep ---

    [Test]
    public async Task RunAsync_DeletesAssetNotReferencedByAnyConnection()
    {
        var ownerId = Guid.NewGuid();
        var orphanId = Guid.NewGuid().ToString("D");
        _certStore.Assets.Add((ownerId, orphanId));

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        _certStore.Deleted.Should().Contain((ownerId, orphanId));
    }

    [Test]
    public async Task RunAsync_KeepsAssetReferencedByAConnection()
    {
        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid().ToString("D");
        _certStore.Assets.Add((ownerId, assetId));
        var connection = new Connection { Id = ownerId, ClientCertificateAssetId = assetId };

        await _sut.RunAsync([connection], configLoadedSuccessfully: true);

        _certStore.Deleted.Should().BeEmpty();
    }

    [Test]
    public async Task RunAsync_DeletesAssetWhoseIdMatchesButOwnerDoesNot()
    {
        var storedOwner = Guid.NewGuid();
        var assetId = Guid.NewGuid().ToString("D");
        _certStore.Assets.Add((storedOwner, assetId));
        var connection = new Connection { Id = Guid.NewGuid(), ClientCertificateAssetId = assetId };

        await _sut.RunAsync([connection], configLoadedSuccessfully: true);

        _certStore.Deleted.Should().Contain((storedOwner, assetId));
    }

    // --- unverified blob sweep ---

    [Test]
    public async Task RunAsync_PreservesUnverifiedBlobWhoseHeaderNamesAKnownConnection()
    {
        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(_certDir, $"cert-{assetId}.bin");
        await File.WriteAllBytesAsync(path, MakeBlob(ownerId));
        var connection = new Connection { Id = ownerId };

        await _sut.RunAsync([connection], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeTrue();
    }

    [Test]
    public async Task RunAsync_DeletesUnverifiedBlobWhoseHeaderNamesAnUnknownConnection()
    {
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(_certDir, $"cert-{assetId}.bin");
        await File.WriteAllBytesAsync(path, MakeBlob(Guid.NewGuid()));

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeFalse();
        _envelopeKeys.Removed.Should().Contain($"cert-env-{assetId}");
    }

    [Test]
    public async Task RunAsync_DeletesBlobTooShortToHoldAHeader()
    {
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(_certDir, $"cert-{assetId}.bin");
        await File.WriteAllBytesAsync(path, new byte[10]);

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeFalse();
        _envelopeKeys.Removed.Should().NotContain($"cert-env-{assetId}");
    }

    [Test]
    public async Task RunAsync_LeavesVerifiedBlobAlone()
    {
        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(_certDir, $"cert-{assetId}.bin");
        // Unknown owner in the blob header: the only reason this file can survive is the
        // verified-asset early-continue. A known owner would also survive via the
        // preserve-and-LogCritical branch, making the test pass whether or not the
        // early-continue guard exists.
        await File.WriteAllBytesAsync(path, MakeBlob(Guid.NewGuid()));
        _certStore.Assets.Add((ownerId, assetId));
        var connection = new Connection { Id = ownerId, ClientCertificateAssetId = assetId };

        await _sut.RunAsync([connection], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeTrue();
    }

    // --- missing directory ---

    [Test]
    public async Task RunAsync_MissingCertificateDirectory_DoesNothingAndDoesNotThrow()
    {
        Directory.Delete(_certDir, recursive: true);

        var act = () => _sut.RunAsync([], configLoadedSuccessfully: true);

        await act.Should().NotThrowAsync();
        _certStore.ListAssetsCallCount.Should().Be(0);
    }

    // Ordinary test blobs use the production header layout; the short-blob test above
    // deliberately remains a manually constructed malformed blob.
    private static byte[] MakeBlob(Guid ownerId)
        => CertificateAssetBlobCodec.BuildHeader(Guid.NewGuid().ToString("D"), ownerId);

    private sealed class FakeAssetStore(string certificatesDirectory) : ICertificateAssetStore
    {
        public List<(Guid OwnerId, string AssetId)> Assets { get; } = [];
        public List<(Guid OwnerId, string AssetId)> Deleted { get; } = [];
        public int ListAssetsCallCount { get; private set; }

        public string CertificatesDirectory => certificatesDirectory;

        public Task<IReadOnlyList<(Guid OwnerId, string AssetId)>> ListAssetsAsync()
        {
            ListAssetsCallCount++;
            return Task.FromResult<IReadOnlyList<(Guid, string)>>(Assets.ToList());
        }

        public Task DeleteAsync(Guid ownerConnectionId, string assetId)
        {
            Deleted.Add((ownerConnectionId, assetId));
            Assets.RemoveAll(a => a.OwnerId == ownerConnectionId && a.AssetId == assetId);
            return Task.CompletedTask;
        }

        public Task<string> ImportAsync(Guid ownerConnectionId, CertificateImportRequest request) =>
            throw new NotSupportedException("Cleanup must never import.");

        public Task<ClientCertificateBundle?> LoadAsync(Guid ownerConnectionId, string assetId) =>
            throw new NotSupportedException("Cleanup must never load a bundle.");
    }

    private sealed class FakeEnvelopeKeyStore : ICertificateEnvelopeKeyStore
    {
        public List<string> Removed { get; } = [];

        public Task RemoveAsync(string key)
        {
            Removed.Add(key);
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value) => Task.CompletedTask;
    }
}
