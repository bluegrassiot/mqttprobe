using System.Security.Cryptography;
using System.Text;
using MqttProbe.Desktop.Services.Security;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.Services.Security.Fakes;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class DesktopSecretKeyProtectorTests
{
    private string _tmpDir = null!;
    private FakeRawSecretKeyFile _fakeRaw = null!;
    private FakeSecretKeyProtector _fakeOs = null!;
    private FileSecretKeyProtector _file = null!;
    private Func<bool> _hasCiphertextFiles = null!;

    [SetUp]
    public void Setup()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mqttprobe-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _fakeRaw = new FakeRawSecretKeyFile();
        _fakeOs = new FakeSecretKeyProtector();
        _file = new FileSecretKeyProtector(_fakeRaw);
        _hasCiphertextFiles = () => Directory.EnumerateFiles(_tmpDir, "*.dat").Any();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private DesktopSecretKeyProtector CreateSut()
        => new(_tmpDir, _fakeOs, _file, _fakeRaw, _hasCiphertextFiles);

    [Test]
    public void Mode_BeforeInit_Throws()
    {
        var sut = CreateSut();
        Assert.Throws<InvalidOperationException>(() => { _ = sut.Mode; });
    }

    [Test]
    public async Task Init_OsKeyFound_NoRaw_ModeOsKeyring()
    {
        var osKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _fakeOs.MemoryKey = osKey;
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.That(sut.Mode, Is.EqualTo(SecretProtectionMode.OsKeyring));
    }

    [Test]
    public async Task Init_FacilityUnavailable_Empty_ModeFileFallback()
    {
        _fakeOs.NextLoad = new SecretKeyLoadResult.FacilityUnavailable();
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.That(sut.Mode, Is.EqualTo(SecretProtectionMode.FileFallback));
    }

    [Test]
    public async Task Init_FacilityUnavailable_RawPresent_FileFallback()
    {
        var rawKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _fakeRaw.Bytes = rawKey;
        _fakeOs.NextLoad = new SecretKeyLoadResult.FacilityUnavailable();
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.That(sut.Mode, Is.EqualTo(SecretProtectionMode.FileFallback));
    }

    [Test]
    public void Init_FacilityUnavailable_DatOnly_ThrowsOrphaned()
    {
        File.WriteAllBytes(Path.Combine(_tmpDir, "x.dat"), new byte[] { 1, 2, 3 });
        _fakeOs.NextLoad = new SecretKeyLoadResult.FacilityUnavailable();
        var sut = CreateSut();

        var ex = Assert.ThrowsAsync<OrphanedSecretStoreException>(
            async () => await sut.InitializeAsync());
        Assert.That(ex.Message, Does.Contain("Ciphertext"));
    }

    [Test]
    public void Init_UnexpectedOsFailure_ThrowsFacility_NoFallback()
    {
        _fakeOs.NextLoad = new SecretKeyLoadResult.UnexpectedFailure(
            new Exception("OS error"));
        var sut = CreateSut();

        var ex = Assert.ThrowsAsync<SecretKeyFacilityException>(
            async () => await sut.InitializeAsync());
        Assert.That(ex.Message, Does.Contain("OS keyring"));
    }

    [Test]
    public async Task Init_MigrateRawToOs_Success_DeletesRaw()
    {
        var rawKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _fakeRaw.Bytes = rawKey;
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.That(sut.Mode, Is.EqualTo(SecretProtectionMode.OsKeyring));
        Assert.That(_fakeRaw.Exists, Is.False);
        Assert.That(_fakeOs.MemoryKey, Is.EqualTo(rawKey));
    }

    [Test]
    public void Init_MigrateStoreOk_DeleteFails_PartialMigration()
    {
        var rawKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _fakeRaw.Bytes = rawKey;
        _fakeRaw.DeleteShouldFail = true;
        var sut = CreateSut();

        Assert.ThrowsAsync<PartialSecretKeyMigrationException>(
            async () => await sut.InitializeAsync());
    }

    [Test]
    public async Task Init_OsAndRawIdentical_DeletesRaw()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _fakeOs.MemoryKey = key;
        _fakeRaw.Bytes = (byte[])key.Clone();
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.That(sut.Mode, Is.EqualTo(SecretProtectionMode.OsKeyring));
        Assert.That(_fakeRaw.Exists, Is.False);
    }

    [Test]
    public async Task Init_OsAndRawDiffer_NoDat_DeletesStaleRaw()
    {
        var osKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        var rawKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _fakeOs.MemoryKey = osKey;
        _fakeRaw.Bytes = rawKey;
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.That(sut.Mode, Is.EqualTo(SecretProtectionMode.OsKeyring));
        Assert.That(_fakeRaw.Exists, Is.False);
        Assert.That(_fakeOs.MemoryKey, Is.EqualTo(osKey));
    }

    [Test]
    public void Init_OsAndRawDiffer_DatExist_Ambiguous()
    {
        File.WriteAllBytes(Path.Combine(_tmpDir, "x.dat"), new byte[] { 1, 2, 3 });
        var osKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        var rawKey = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _fakeOs.MemoryKey = osKey;
        _fakeRaw.Bytes = rawKey;
        var sut = CreateSut();

        var ex = Assert.ThrowsAsync<AmbiguousSecretKeyException>(
            async () => await sut.InitializeAsync());
        Assert.That(ex.Message, Does.Contain("OS keyring"));
    }

    [Test]
    public void Init_OsNotFound_DatExist_NoRaw_Orphaned()
    {
        File.WriteAllBytes(Path.Combine(_tmpDir, "x.dat"), new byte[] { 1, 2, 3 });
        var sut = CreateSut();

        var ex = Assert.ThrowsAsync<OrphanedSecretStoreException>(
            async () => await sut.InitializeAsync());
        Assert.That(ex.Message, Does.Contain("Ciphertext"));
    }

    [Test]
    public void Init_RawKeyWrongLength_Throws()
    {
        _fakeRaw.Bytes = new byte[16];
        var sut = CreateSut();

        Assert.ThrowsAsync<SecretStorageException>(
            async () => await sut.InitializeAsync());
    }

    [Test]
    public async Task GetOrCreateKey_FirstWrite_StoreThenReadBack()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var key = await sut.GetOrCreateKeyAsync();

        Assert.That(key, Has.Length.EqualTo(MasterKeyConstants.KeySize));
        Assert.That(_fakeOs.StoreCallCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetOrCreateKey_ConcurrentFirstWrites_SameKey()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        var results = await Task.WhenAll(
            sut.GetOrCreateKeyAsync(),
            sut.GetOrCreateKeyAsync());

        Assert.That(results, Has.Length.EqualTo(2));
        Assert.That(results[0], Is.EqualTo(results[1]));
        Assert.That(results[0], Has.Length.EqualTo(MasterKeyConstants.KeySize));
    }

    [Test]
    public void GetOrCreateKey_BeforeInit_Throws()
    {
        var sut = CreateSut();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.GetOrCreateKeyAsync());
        Assert.That(ex.Message, Does.Contain("not been initialized"));
    }

    [Test]
    public void Init_OsNotFound_LegacyDpapiDat_AllowsInit()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("DPAPI is Windows-only.");

        var plain = Encoding.UTF8.GetBytes("legacy-secret");
#pragma warning disable CA1416
        var blob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        File.WriteAllBytes(Path.Combine(_tmpDir, "x.dat"), blob);
        var sut = CreateSut();

        Assert.DoesNotThrowAsync(async () => await sut.InitializeAsync());
        Assert.That(sut.Mode, Is.EqualTo(SecretProtectionMode.OsKeyring));
    }

    [Test]
    public void Init_OsNotFound_MixedDpapiAndNonDpapiDat_ThrowsOrphaned()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("DPAPI is Windows-only.");

        var plain = Encoding.UTF8.GetBytes("legacy-secret");
#pragma warning disable CA1416
        var dpapiBlob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        File.WriteAllBytes(Path.Combine(_tmpDir, "legacy.dat"), dpapiBlob);
        File.WriteAllBytes(Path.Combine(_tmpDir, "modern.dat"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        var sut = CreateSut();

        var ex = Assert.ThrowsAsync<OrphanedSecretStoreException>(
            async () => await sut.InitializeAsync());
        Assert.That(ex.Message, Does.Contain("Ciphertext"));
    }
}
