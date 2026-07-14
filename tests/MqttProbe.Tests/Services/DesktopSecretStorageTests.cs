using System.Text;
using MqttProbe.Services;

namespace MqttProbe.Shared.Tests.Services;

[TestFixture]
public class DesktopSecretStorageTests
{
    private string _dir = null!;
    private DesktopSecretStorage _store = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mqttprobe-secrets-test-" + Guid.NewGuid().ToString("N"));
        _store = new DesktopSecretStorage(_dir);
    }

    [TearDown]
    public void Teardown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task SetThenGet_RoundTripsValue()
    {
        await _store.SetAsync("broker/password", "s3cr3t-p@ss");

        var value = await _store.GetAsync("broker/password");

        Assert.That(value, Is.EqualTo("s3cr3t-p@ss"));
    }

    [Test]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var value = await _store.GetAsync("does/not/exist");

        Assert.That(value, Is.Null);
    }

    [Test]
    public async Task SetEmpty_RemovesExistingSecret()
    {
        await _store.SetAsync("k", "value");

        await _store.SetAsync("k", "");

        Assert.That(await _store.GetAsync("k"), Is.Null);
    }

    [Test]
    public async Task Set_EncryptsAtRest_PlaintextNotOnDisk()
    {
        const string secret = "plaintext-should-not-appear";
        await _store.SetAsync("k", secret);

        var onDisk = Directory.EnumerateFiles(_dir)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        var haystack = Encoding.UTF8.GetString(onDisk);

        Assert.That(haystack, Does.Not.Contain(secret));
    }

    [Test]
    public async Task Get_CorruptSecretFile_FailsSoftReturnsNull()
    {
        await _store.SetAsync("k", "value");
        // Corrupt every stored secret blob (leave the key file intact).
        foreach (var file in Directory.EnumerateFiles(_dir, "*.dat"))
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        Assert.That(await _store.GetAsync("k"), Is.Null);
    }

    [Test]
    public async Task Get_AfterReopen_StillDecrypts()
    {
        await _store.SetAsync("k", "durable");

        // New instance over the same directory reuses the persisted key.
        var reopened = new DesktopSecretStorage(_dir);
        Assert.That(await reopened.GetAsync("k"), Is.EqualTo("durable"));
    }
}
