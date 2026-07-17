using System.Security.Cryptography;
using MqttProbe.Desktop.Services.Security;
using MqttProbe.Shared.Tests.Services.Security.Fakes;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class LinuxLibsecretKeyProtectorTests
{
    private FakeLinuxLibsecretNative _native = null!;

    [SetUp]
    public void Setup()
    {
        _native = new FakeLinuxLibsecretNative();
    }

    [Test]
    public async Task Load_ValidBase64_ReturnsFound()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.LookupCode = 0;
        _native.LookupPassword = Convert.ToBase64String(key);

        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.Found>());
        Assert.That(((SecretKeyLoadResult.Found)result).Key, Is.EqualTo(key));
    }

    [Test]
    public async Task Load_NotFound_ReturnsNotFound()
    {
        _native.LookupCode = 1;

        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.NotFound>());
    }

    [Test]
    public async Task Load_Unavailable_ReturnsFacilityUnavailable()
    {
        _native.LookupCode = 2;
        _native.LookupError = "libsecret not available";

        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.FacilityUnavailable>());
    }

    [Test]
    public async Task Load_BadBase64_ReturnsUnexpectedFailure()
    {
        _native.LookupCode = 0;
        _native.LookupPassword = "not-valid-base64!!!";

        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Load_Base64Of16Bytes_ReturnsUnexpectedFailure()
    {
        _native.LookupCode = 0;
        _native.LookupPassword = Convert.ToBase64String(new byte[16]);

        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Store_Success_ReturnsStored()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.StoreCode = 0;

        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: true);
        var result = await sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.Stored>());
        Assert.That(_native.StoreCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Store_WrongLength_ReturnsUnexpectedFailure()
    {
        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: true);
        var result = await sut.StoreAsync(new byte[16]);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.UnexpectedFailure>());
    }

    [Test]
    public async Task NonLinux_ReturnsFacilityUnavailable()
    {
        var sut = new LinuxLibsecretKeyProtector(_native, isLinux: false);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.FacilityUnavailable>());
        Assert.That(_native.LookupCallCount, Is.EqualTo(0));
    }
}

[TestFixture]
public class LinuxLibsecretNativeTests
{
    [Test]
    public void ClassifyLibsecretError_NoError_ReturnsUnexpectedFailure()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(null, hasError: false);
        Assert.That(code, Is.EqualTo(3));
    }

    [Test]
    public void ClassifyLibsecretError_NullMessage_ReturnsUnexpectedFailure()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(null, hasError: true);
        Assert.That(code, Is.EqualTo(3));
    }

    [Test]
    public void ClassifyLibsecretError_CannotAutolaunch_ReturnsFacilityUnavailable()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "Cannot autolaunch D-Bus without X11 $DISPLAY", hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public void ClassifyLibsecretError_ServiceUnknown_ReturnsFacilityUnavailable()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "The name org.freedesktop.secrets was not provided by any .service files",
            hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public void ClassifyLibsecretError_NoSuchService_ReturnsFacilityUnavailable()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "No such service", hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public void ClassifyLibsecretError_NoDBus_ReturnsFacilityUnavailable()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "No D-Bus daemon running", hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public void ClassifyLibsecretError_NotAvailable_ReturnsFacilityUnavailable()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "Secret service is not available", hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public void ClassifyLibsecretError_NoSession_ReturnsFacilityUnavailable()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "No session available", hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public void ClassifyLibsecretError_SpawnFailed_ReturnsFacilityUnavailable()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "Failed to spawn secrets service", hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public void ClassifyLibsecretError_GenericError_ReturnsUnexpectedFailure()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "Something went wrong", hasError: true);
        Assert.That(code, Is.EqualTo(3));
    }

    [Test]
    public void ClassifyLibsecretError_CaseInsensitive()
    {
        var code = LinuxLibsecretNative.ClassifyLibsecretError(
            "CANNOT AUTOLAUNCH D-BUS", hasError: true);
        Assert.That(code, Is.EqualTo(2));
    }
}
