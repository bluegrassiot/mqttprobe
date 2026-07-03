using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MqttProbe.Services;
using NSubstitute;
using NUnit.Framework;

namespace MqttProbe.Shared.Tests.Desktop;

[TestFixture]
public class DesktopClipboardServiceTests
{
    private IJSRuntime _js = null!;
    private DesktopClipboardService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _js = Substitute.For<IJSRuntime>();
        _sut = new DesktopClipboardService(_js);
    }

    [Test]
    public async Task GetTextAsync_returns_text_from_js_clipboard()
    {
        _js.InvokeAsync<string?>("navigator.clipboard.readText", Arg.Any<object?[]?>())
            .Returns(new ValueTask<string?>("hello"));

        var result = await _sut.GetTextAsync();

        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public async Task GetTextAsync_falls_back_to_native_when_js_throws()
    {
        _js.InvokeAsync<string?>("navigator.clipboard.readText", Arg.Any<object?[]?>())
            .Returns(ValueTask.FromException<string?>(new JSException("denied")));
        _sut.ReadNativeFallback = () => Task.FromResult<string?>("native");

        var result = await _sut.GetTextAsync();

        Assert.That(result, Is.EqualTo("native"));
    }

    [Test]
    public async Task GetTextAsync_falls_back_to_native_when_js_returns_empty()
    {
        _js.InvokeAsync<string?>("navigator.clipboard.readText", Arg.Any<object?[]?>())
            .Returns(new ValueTask<string?>(string.Empty));
        _sut.ReadNativeFallback = () => Task.FromResult<string?>("native");

        var result = await _sut.GetTextAsync();

        Assert.That(result, Is.EqualTo("native"));
    }

    [Test]
    public async Task WriteTextAsync_writes_via_js_clipboard()
    {
        await _sut.WriteTextAsync("copy me");

        await _js.Received(1).InvokeAsync<IJSVoidResult>(
            "navigator.clipboard.writeText",
            Arg.Is<object?[]?>(a => a != null && Equals(a[0], "copy me")));
    }

    [Test]
    public async Task WriteTextAsync_falls_back_to_native_when_js_throws()
    {
        _js.InvokeAsync<IJSVoidResult>("navigator.clipboard.writeText", Arg.Any<object?[]?>())
            .Returns(ValueTask.FromException<IJSVoidResult>(new JSException("denied")));
        string? written = null;
        _sut.WriteNativeFallback = t => { written = t; return Task.CompletedTask; };

        await _sut.WriteTextAsync("copy me");

        Assert.That(written, Is.EqualTo("copy me"));
    }
}
