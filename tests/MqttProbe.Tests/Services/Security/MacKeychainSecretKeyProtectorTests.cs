using System.Security.Cryptography;
using MqttProbe.Desktop.Services.Security;
using MqttProbe.Shared.Tests.Services.Security.Fakes;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class MacKeychainSecretKeyProtectorTests
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecInteractionNotAllowed = -25308;
    private const int ErrSecNotAvailable = -25291;
    private const int ErrSecMissingEntitlement = -34018;

    private FakeMacKeychainNative _native = null!;

    [SetUp]
    public void Setup()
    {
        _native = new FakeMacKeychainNative();
    }

    [Test]
    public async Task Load_Success_ReturnsFound()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.CopyMatchingStatus = ErrSecSuccess;
        _native.CopyMatchingData = key;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.Found>());
        Assert.That(((SecretKeyLoadResult.Found)result).Key, Is.EqualTo(key));
        Assert.That(_native.LastService, Is.EqualTo(MasterKeyConstants.AppServiceId));
        Assert.That(_native.LastAccount, Is.EqualTo(MasterKeyConstants.KeyVersionId));
    }

    [Test]
    public async Task Load_ItemNotFound_ReturnsNotFound()
    {
        _native.CopyMatchingStatus = ErrSecItemNotFound;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.NotFound>());
    }

    [Test]
    public async Task Load_InteractionNotAllowed_ReturnsFacilityUnavailable()
    {
        _native.CopyMatchingStatus = ErrSecInteractionNotAllowed;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.FacilityUnavailable>());
    }

    [Test]
    public async Task Load_NotAvailable_ReturnsFacilityUnavailable()
    {
        _native.CopyMatchingStatus = ErrSecNotAvailable;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.FacilityUnavailable>());
    }

    [Test]
    public async Task Load_MissingEntitlement_ReturnsFacilityUnavailable()
    {
        _native.CopyMatchingStatus = ErrSecMissingEntitlement;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.FacilityUnavailable>());
    }

    [Test]
    public async Task Load_SuccessWrongLength_ReturnsUnexpectedFailure()
    {
        _native.CopyMatchingStatus = ErrSecSuccess;
        _native.CopyMatchingData = new byte[16];

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Load_SuccessNullData_ReturnsUnexpectedFailure()
    {
        _native.CopyMatchingStatus = ErrSecSuccess;
        _native.CopyMatchingData = null;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Load_UnknownStatus_ReturnsUnexpectedFailure()
    {
        _native.CopyMatchingStatus = -99999;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Store_UpdateSuccess_ReturnsStored()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.UpdateStatus = ErrSecSuccess;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.Stored>());
        Assert.That(_native.UpdateCallCount, Is.EqualTo(1));
        Assert.That(_native.AddCallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Store_UpdateNotFoundThenAddSuccess_ReturnsStored()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.UpdateStatus = ErrSecItemNotFound;
        _native.AddStatus = ErrSecSuccess;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.Stored>());
        Assert.That(_native.UpdateCallCount, Is.EqualTo(1));
        Assert.That(_native.AddCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Store_UpdateNotFoundThenAddFails_ReturnsUnexpectedFailure()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.UpdateStatus = ErrSecItemNotFound;
        _native.AddStatus = -99999;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Store_WrongKeyLength_ReturnsUnexpectedFailure()
    {
        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.StoreAsync(new byte[16]);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Store_UnknownStatus_ReturnsUnexpectedFailure()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.UpdateStatus = -99999;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.UnexpectedFailure>());
    }

    [Test]
    public async Task Store_FacilityUnavailable_ReturnsFacilityUnavailable()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        _native.UpdateStatus = ErrSecInteractionNotAllowed;

        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: true);
        var result = await sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.FacilityUnavailable>());
    }

    [Test]
    public async Task NonMacOS_Load_ReturnsFacilityUnavailable()
    {
        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: false);
        var result = await sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.FacilityUnavailable>());
    }

    [Test]
    public async Task NonMacOS_Store_ReturnsFacilityUnavailable()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);
        var sut = new MacKeychainSecretKeyProtector(_native, isMacOs: false);
        var result = await sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.FacilityUnavailable>());
    }
}
