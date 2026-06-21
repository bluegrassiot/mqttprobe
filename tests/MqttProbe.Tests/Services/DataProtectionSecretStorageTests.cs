using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MqttProbe.Services.Chart;
using MqttProbe.Services.Configuration;
using MqttProbe.Services.Mqtt;
using MqttProbe.Services.Platform;
using MqttProbe.Services.Security;
using MqttProbe.Services.Sparkplug;
using MqttProbe.Services.Telemetry;
using MqttProbe.Web.Services;

namespace MqttProbe.Shared.Tests.Services;

[TestFixture]
public class DataProtectionSecretStorageTests
{
    private IDataProtectionProvider _provider = null!;
    private string _storePath = null!;

    [SetUp]
    public void Setup()
    {
        _storePath = Path.GetTempFileName();
        File.Delete(_storePath); // start without file

        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        _provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    private DataProtectionSecretStorage CreateSut() =>
        new(_provider, _storePath);

    [Test]
    public async Task StoreAsync_WritesEncryptedFileToPath()
    {
        var sut = CreateSut();
        await sut.SetAsync("key1", "value1");
        File.Exists(_storePath).Should().BeTrue();
    }

    [Test]
    public async Task RetrieveAsync_ReturnsStoredValue()
    {
        var sut = CreateSut();
        await sut.SetAsync("mykey", "myvalue");
        var result = await sut.GetAsync("mykey");
        result.Should().Be("myvalue");
    }

    [Test]
    public async Task RetrieveAsync_ReturnsNull_WhenKeyNotPresent()
    {
        var sut = CreateSut();
        var result = await sut.GetAsync("nonexistent");
        result.Should().BeNull();
    }

    [Test]
    public async Task RetrieveAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var sut = CreateSut(); // no file written yet
        var result = await sut.GetAsync("anything");
        result.Should().BeNull();
    }

    [Test]
    public async Task StoreAsync_MultipleKeys_AllRetrievable()
    {
        var sut = CreateSut();
        await sut.SetAsync("a", "alpha");
        await sut.SetAsync("b", "beta");
        await sut.SetAsync("c", "gamma");

        (await sut.GetAsync("a")).Should().Be("alpha");
        (await sut.GetAsync("b")).Should().Be("beta");
        (await sut.GetAsync("c")).Should().Be("gamma");
    }

    [Test]
    public async Task StoreAsync_UpdatesExistingKey()
    {
        var sut = CreateSut();
        await sut.SetAsync("key", "original");
        await sut.SetAsync("key", "updated");
        (await sut.GetAsync("key")).Should().Be("updated");
    }

    [Test]
    public async Task DeleteAsync_RemovesKey()
    {
        var sut = CreateSut();
        await sut.SetAsync("toDelete", "bye");
        await sut.RemoveAsync("toDelete");
        (await sut.GetAsync("toDelete")).Should().BeNull();
    }

    [Test]
    public async Task GetAsync_ThrowsInvalidOperation_WhenFileCorrupt()
    {
        File.WriteAllText(_storePath, "this is not encrypted data %%%");
        var sut = CreateSut();
        var act = async () => await sut.GetAsync("key");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task SetAsync_ThrowsAndDoesNotOverwriteFile_WhenUnprotectFails()
    {
        var corruptContent = "this is not valid encrypted data %%%";
        File.WriteAllText(_storePath, corruptContent);
        var sut = CreateSut();

        var act = async () => await sut.SetAsync("key", "value");

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(_storePath).Should().Be(corruptContent, "the corrupt file must not be overwritten");
    }

    [Test]
    public async Task File_IsNotPlaintext()
    {
        var sut = CreateSut();
        const string secret = "super-secret-password-12345";
        await sut.SetAsync("pw", secret);

        var raw = File.ReadAllText(_storePath);
        raw.Should().NotContain(secret);
    }
}
