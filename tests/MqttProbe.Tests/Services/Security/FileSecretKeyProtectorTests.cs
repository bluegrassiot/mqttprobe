using System.Security.Cryptography;
using MqttProbe.Desktop.Services.Security;
using MqttProbe.Shared.Tests.Services.Security.Fakes;

namespace MqttProbe.Shared.Tests.Services.Security;

[TestFixture]
public class FileSecretKeyProtectorTests
{
    private FakeRawSecretKeyFile _fakeRaw = null!;
    private FileSecretKeyProtector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _fakeRaw = new FakeRawSecretKeyFile();
        _sut = new FileSecretKeyProtector(_fakeRaw);
    }

    [Test]
    public async Task Load_WhenMissing_ReturnsNotFound()
    {
        _fakeRaw.Bytes = null;

        var result = await _sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.NotFound>());
    }

    [Test]
    public async Task StoreThenLoad_RoundTrips32ByteKey()
    {
        var key = RandomNumberGenerator.GetBytes(MasterKeyConstants.KeySize);

        var storeResult = await _sut.StoreAsync(key);
        Assert.That(storeResult, Is.TypeOf<SecretKeyStoreResult.Stored>());

        var loadResult = await _sut.LoadAsync();
        Assert.That(loadResult, Is.TypeOf<SecretKeyLoadResult.Found>());
        var found = (SecretKeyLoadResult.Found)loadResult;
        Assert.That(found.Key, Is.EqualTo(key));
    }

    [Test]
    public async Task Store_WrongLength_ReturnsUnexpectedFailure()
    {
        var key = RandomNumberGenerator.GetBytes(16);

        var result = await _sut.StoreAsync(key);

        Assert.That(result, Is.TypeOf<SecretKeyStoreResult.UnexpectedFailure>());
        var failure = (SecretKeyStoreResult.UnexpectedFailure)result;
        Assert.That(failure.Error, Is.TypeOf<SecretStorageException>());
    }

    [Test]
    public async Task Load_WhenRawHasWrongLength_ReturnsUnexpectedFailure()
    {
        _fakeRaw.Bytes = RandomNumberGenerator.GetBytes(16);

        var result = await _sut.LoadAsync();

        Assert.That(result, Is.TypeOf<SecretKeyLoadResult.UnexpectedFailure>());
        var failure = (SecretKeyLoadResult.UnexpectedFailure)result;
        Assert.That(failure.Error, Is.TypeOf<SecretStorageException>());
    }
}
