using MqttProbe.Services.Security;
using MqttProbe.Tests.Services.Security.TestHelpers;

namespace MqttProbe.Tests.Services.Security;

[TestFixture]
public class PlatformAdapterTests
{
    [Test]
    public async Task EnvelopeKeyStore_Contract_GetSetRemoveRoundTrip()
    {
        ICertificateEnvelopeKeyStore store = new InMemoryEnvelopeKeyStore();

        await store.SetAsync("key1", "value1");
        (await store.GetAsync("key1")).Should().Be("value1");

        await store.RemoveAsync("key1");
        (await store.GetAsync("key1")).Should().BeNull();
    }

    [Test]
    public async Task EnvelopeKeyStore_Contract_GetNonExistent_ReturnsNull()
    {
        ICertificateEnvelopeKeyStore store = new InMemoryEnvelopeKeyStore();
        (await store.GetAsync("nonexistent")).Should().BeNull();
    }

    [Test]
    public async Task EnvelopeKeyStore_Contract_SetOverwrites()
    {
        ICertificateEnvelopeKeyStore store = new InMemoryEnvelopeKeyStore();

        await store.SetAsync("key1", "value1");
        await store.SetAsync("key1", "value2");
        (await store.GetAsync("key1")).Should().Be("value2");
    }

    [Test]
    public void FilePicker_Contract_WebReturnsNull()
    {
        ICertificateFilePicker picker = new AlwaysNullFilePicker();
        var result = picker.PickFileAsync("test", [".pfx"], 1024).Result;
        result.Should().BeNull();
    }

    [Test]
    public void InputCapability_Contract_ReflectsPlatform()
    {
        ICertificateInputCapability webLike = new StubInputCapability(usesInputFile: true);
        webLike.UsesInputFileComponent.Should().BeTrue();

        ICertificateInputCapability desktopLike = new StubInputCapability(usesInputFile: false);
        desktopLike.UsesInputFileComponent.Should().BeFalse();
    }
}

internal class AlwaysNullFilePicker : ICertificateFilePicker
{
    public Task<byte[]?> PickFileAsync(string title, string[] extensions, long maxBytes)
        => Task.FromResult<byte[]?>(null);
}

internal class StubInputCapability : ICertificateInputCapability
{
    private readonly bool _usesInputFile;
    public StubInputCapability(bool usesInputFile) => _usesInputFile = usesInputFile;
    public bool UsesInputFileComponent => _usesInputFile;
}
