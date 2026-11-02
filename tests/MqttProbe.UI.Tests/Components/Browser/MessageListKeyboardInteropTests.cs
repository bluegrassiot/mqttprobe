using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class MessageListKeyboardInteropTests
{
    private const string ModuleUrl = "./_content/MqttProbe.UI/js/message-list-keyboard.js";

    [Test]
    public async Task Reveal_PassesTheListAndIndex()
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);
        var list = new ElementReference("message-list");

        await interop.RevealAsync(list, 7);

        runtime.ImportedPaths.Should().Equal(ModuleUrl);
        runtime.Module.Identifiers.Should().Equal("reveal");
        runtime.Module.Arguments[0].Should().Equal(list, 7);
    }

    [Test]
    public async Task Reveal_ForNoRow_DoesNothing()
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);

        await interop.RevealAsync(default, -1);

        runtime.ImportedPaths.Should().BeEmpty();
        runtime.Module.Identifiers.Should().BeEmpty();
    }

    [Test]
    public async Task Reveal_ImportsTheModuleWithoutAttaching()
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);

        await interop.RevealAsync(default, 0);
        await interop.DisposeAsync();

        runtime.Module.Identifiers.Should().Equal("reveal");
        runtime.Module.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task Reveal_AfterDispose_DoesNothing()
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);
        await interop.RevealAsync(default, 0);
        await interop.DisposeAsync();

        await interop.RevealAsync(default, 0);

        runtime.Module.Identifiers.Should().Equal("reveal");
        runtime.Module.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task Reveal_WhenImportDisconnects_Propagates()
    {
        var runtime = new FakeKeyboardJSRuntime { DisconnectedOnImport = true };
        var interop = new MessageListKeyboardInterop(runtime);

        Func<Task> reveal = () => interop.RevealAsync(default, 0);

        await reveal.Should().ThrowAsync<JSDisconnectedException>();
        runtime.ImportedPaths.Should().Equal(ModuleUrl);
        runtime.Module.Identifiers.Should().BeEmpty();
    }
}
