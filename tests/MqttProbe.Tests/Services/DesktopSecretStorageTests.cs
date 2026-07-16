using System.Security.Cryptography;
using System.Text;
using MqttProbe.Desktop.Services.Security;
using MqttProbe.Services;
using MqttProbe.Services.Security;
using MqttProbe.Shared.Tests.Services.Security.Fakes;

namespace MqttProbe.Shared.Tests.Services;

[TestFixture]
public class DesktopSecretStorageTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mqttprobe-secrets-test-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void Teardown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(DesktopSecretStorage Store, DesktopSecretKeyProtector Protector, FakeSecretKeyProtector FakeOs, RawSecretKeyFile Raw)> CreateFileFallbackStoreAsync()
    {
        var raw = new RawSecretKeyFile(_dir);
        var file = new FileSecretKeyProtector(raw);
        var fakeOs = new FakeSecretKeyProtector { NextLoad = new SecretKeyLoadResult.FacilityUnavailable() };
        var protector = new DesktopSecretKeyProtector(_dir, fakeOs, file, raw);
        await protector.InitializeAsync();
        var store = new DesktopSecretStorage(_dir, protector);
        return (store, protector, fakeOs, raw);
    }

    [Test]
    public async Task SetThenGet_RoundTripsValue()
    {
        var (store, _, _, _) = await CreateFileFallbackStoreAsync();

        await store.SetAsync("broker/password", "s3cr3t-p@ss");

        var value = await store.GetAsync("broker/password");
        Assert.That(value, Is.EqualTo("s3cr3t-p@ss"));
    }

    [Test]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var (store, _, _, _) = await CreateFileFallbackStoreAsync();

        var value = await store.GetAsync("does/not/exist");
        Assert.That(value, Is.Null);
    }

    [Test]
    public async Task SetEmpty_RemovesExistingSecret()
    {
        var (store, _, _, _) = await CreateFileFallbackStoreAsync();

        await store.SetAsync("k", "value");
        await store.SetAsync("k", "");

        Assert.That(await store.GetAsync("k"), Is.Null);
    }

    [Test]
    public async Task Set_EncryptsAtRest_PlaintextNotOnDisk()
    {
        var (store, _, _, _) = await CreateFileFallbackStoreAsync();

        const string secret = "plaintext-should-not-appear";
        await store.SetAsync("k", secret);

        var onDisk = Directory.EnumerateFiles(_dir)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        var haystack = Encoding.UTF8.GetString(onDisk);

        Assert.That(haystack, Does.Not.Contain(secret));
    }

    [Test]
    public async Task Get_CorruptSecretFile_FailsSoftReturnsNull()
    {
        var (store, _, _, _) = await CreateFileFallbackStoreAsync();

        await store.SetAsync("k", "value");
        foreach (var file in Directory.EnumerateFiles(_dir, "*.dat"))
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        Assert.That(await store.GetAsync("k"), Is.Null);
    }

    [Test]
    public async Task Get_AfterReopen_StillDecrypts()
    {
        var (store, _, _, _) = await CreateFileFallbackStoreAsync();
        await store.SetAsync("k", "durable");

        var raw = new RawSecretKeyFile(_dir);
        var file = new FileSecretKeyProtector(raw);
        var fakeOs = new FakeSecretKeyProtector { NextLoad = new SecretKeyLoadResult.FacilityUnavailable() };
        var protector = new DesktopSecretKeyProtector(_dir, fakeOs, file, raw);
        await protector.InitializeAsync();
        var reopened = new DesktopSecretStorage(_dir, protector);

        Assert.That(await reopened.GetAsync("k"), Is.EqualTo("durable"));
    }

    [Test]
    public void Set_WhenKeyFacilityFails_Propagates()
    {
        var raw = new RawSecretKeyFile(_dir);
        var file = new FileSecretKeyProtector(raw);
        var fakeOs = new FakeSecretKeyProtector
        {
            NextLoad = new SecretKeyLoadResult.NotFound(),
            NextStore = new SecretKeyStoreResult.UnexpectedFailure(new Exception("nope"))
        };
        var protector = new DesktopSecretKeyProtector(_dir, fakeOs, file, raw);

        Assert.DoesNotThrowAsync(async () => await protector.InitializeAsync());

        var store = new DesktopSecretStorage(_dir, protector);
        Assert.ThrowsAsync<SecretKeyFacilityException>(async () => await store.SetAsync("k", "v"));
    }

    [Test]
    public async Task Migrate_PreExistingDat_StillDecrypts()
    {
        // Phase 1: file fallback write
        var raw1 = new RawSecretKeyFile(_dir);
        var file1 = new FileSecretKeyProtector(raw1);
        var osUnavailable = new FakeSecretKeyProtector { NextLoad = new SecretKeyLoadResult.FacilityUnavailable() };
        var p1 = new DesktopSecretKeyProtector(_dir, osUnavailable, file1, raw1);
        await p1.InitializeAsync();
        var s1 = new DesktopSecretStorage(_dir, p1);
        await s1.SetAsync("k", "durable-secret");
        Assert.That(File.Exists(Path.Combine(_dir, ".key")), Is.True);

        // Phase 2: OS available, migrate
        var rawKeyBytes = File.ReadAllBytes(Path.Combine(_dir, ".key"));
        var osReady = new FakeSecretKeyProtector { NextLoad = new SecretKeyLoadResult.NotFound() };
        var raw2 = new RawSecretKeyFile(_dir);
        var p2 = new DesktopSecretKeyProtector(_dir, osReady, new FileSecretKeyProtector(raw2), raw2);
        await p2.InitializeAsync();
        Assert.That(p2.Mode, Is.EqualTo(SecretProtectionMode.OsKeyring));
        Assert.That(File.Exists(Path.Combine(_dir, ".key")), Is.False);
        Assert.That(osReady.MemoryKey, Is.EqualTo(rawKeyBytes));

        var s2 = new DesktopSecretStorage(_dir, p2);
        Assert.That(await s2.GetAsync("k"), Is.EqualTo("durable-secret"));
    }

    [Test]
    public async Task Get_LegacyDpapiSecret_MigratesToAes()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("DPAPI is Windows-only.");

        // Write a DPAPI-protected blob directly to disk
        var plain = Encoding.UTF8.GetBytes("legacy-secret");
#pragma warning disable CA1416
        var blob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        var path = Path.Combine(_dir, "bGVnYWN5LWtleQ.dat");
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(path, blob);

        // Init with OS keyring (no OS key yet, will create one)
        var raw = new RawSecretKeyFile(_dir);
        var file = new FileSecretKeyProtector(raw);
        var fakeOs = new FakeSecretKeyProtector { NextLoad = new SecretKeyLoadResult.NotFound() };
        var protector = new DesktopSecretKeyProtector(_dir, fakeOs, file, raw);
        await protector.InitializeAsync();
        var store = new DesktopSecretStorage(_dir, protector);

        // First read migrates from DPAPI to AES
        var value = await store.GetAsync("legacy-key");
        Assert.That(value, Is.EqualTo("legacy-secret"));

        // Second read still works (now AES)
        value = await store.GetAsync("legacy-key");
        Assert.That(value, Is.EqualTo("legacy-secret"));

        // File on disk is no longer a DPAPI blob (it's AES now)
        var onDisk = await File.ReadAllBytesAsync(path);
#pragma warning disable CA1416
        Assert.Throws<CryptographicException>(() =>
            ProtectedData.Unprotect(onDisk, null, DataProtectionScope.CurrentUser));
#pragma warning restore CA1416
    }
}
