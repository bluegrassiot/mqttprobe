using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MqttProbe.Models.Mqtt;
using MqttProbe.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security;

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
        _clock = new FakeTimeProvider();
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
    public async Task RunAsync_DeletesRetryMarkerSittingBesideADeletedStagingTemp()
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
        await File.WriteAllBytesAsync(path, new byte[72]);

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_LeavesVerifiedBlobAlone()
    {
        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid().ToString("D");
        var path = Path.Combine(_certDir, $"cert-{assetId}.bin");
        await File.WriteAllBytesAsync(path, MakeBlob(ownerId));
        _certStore.Assets.Add((ownerId, assetId));
        var connection = new Connection { Id = ownerId, ClientCertificateAssetId = assetId };

        await _sut.RunAsync([connection], configLoadedSuccessfully: true);

        File.Exists(path).Should().BeTrue();
    }

    // The suffix guard exists because Windows matches "cert-x.bin.tmp" against the
    // "cert-*.bin" glob via legacy 8.3 short names. Asserting on a .quarantine file would
    // be vacuous — that glob never returns it on any platform.
    [Test]
    public async Task RunAsync_UnverifiedBlobSweepSkipsStagingTempRatherThanTreatingItAsABlob()
    {
        var assetId = Guid.NewGuid().ToString("D");
        var tmp = Path.Combine(_certDir, $"cert-{assetId}.bin.tmp");
        await File.WriteAllBytesAsync(tmp, MakeBlob(Guid.NewGuid()));

        await _sut.RunAsync([], configLoadedSuccessfully: true);

        // Deleted by stage 1 as a staging temp, not by the blob sweep, and with no
        // "failed to process unverified blob" noise on the way through.
        File.Exists(tmp).Should().BeFalse();
        _envelopeKeys.Removed.Should().Contain($"cert-env-{assetId}");
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

    // A blob laid out the way SweepUnverifiedBlobsAsync reads it: 36 bytes of anything,
    // then the owner id as 36 ASCII bytes, then at least one more byte to clear the
    // 73-byte minimum length check.
    private static byte[] MakeBlob(Guid ownerId)
    {
        var blob = new byte[128];
        System.Text.Encoding.ASCII.GetBytes(ownerId.ToString("D")).CopyTo(blob, 36);
        return blob;
    }

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
