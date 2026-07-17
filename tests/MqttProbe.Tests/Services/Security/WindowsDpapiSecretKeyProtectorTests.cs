using System.Security.Cryptography;
using MqttProbe.Desktop.Services.Security;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class WindowsDpapiSecretKeyProtectorTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mqttprobe-dpapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void Teardown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Test]
    public async Task StoreThenLoad_RoundTrips_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("DPAPI is Windows-only");

        var sut = new WindowsDpapiSecretKeyProtector(_dir);
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);

        Assert.That(await sut.StoreAsync(key), Is.TypeOf<SecretKeyStoreResult.Stored>());
        var load = await sut.LoadAsync();
        Assert.That(load, Is.TypeOf<SecretKeyLoadResult.Found>());
        Assert.That(((SecretKeyLoadResult.Found)load).Key, Is.EqualTo(key));

        var blobPath = Path.Combine(_dir, MasterKeyConstants.WindowsDpapiBlobFileName);
        Assert.That(File.Exists(blobPath), Is.True);
        Assert.That(File.ReadAllBytes(blobPath), Is.Not.EqualTo(key));
    }

    [Test]
    public async Task Load_Missing_ReturnsNotFound_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("DPAPI is Windows-only");

        var sut = new WindowsDpapiSecretKeyProtector(_dir);
        Assert.That(await sut.LoadAsync(), Is.TypeOf<SecretKeyLoadResult.NotFound>());
    }

    [Test]
    public async Task Store_WrongLength_UnexpectedFailure()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("DPAPI is Windows-only");

        var sut = new WindowsDpapiSecretKeyProtector(_dir);
        var result = await sut.StoreAsync(new byte[16]);
        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.UnexpectedFailure>());
    }

    [Test]
    public async Task NonWindows_ReturnsFacilityUnavailable()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("This test is for non-Windows");

        var sut = new WindowsDpapiSecretKeyProtector(_dir);
        Assert.That(await sut.LoadAsync(), Is.TypeOf<SecretKeyLoadResult.FacilityUnavailable>());
        Assert.That(await sut.StoreAsync(new byte[32]), Is.TypeOf<SecretKeyStoreResult.FacilityUnavailable>());
    }
}
