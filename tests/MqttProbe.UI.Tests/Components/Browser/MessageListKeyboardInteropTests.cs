using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class MessageListKeyboardInteropTests
{
    private const string ModuleUrl = "./_content/MqttProbe.UI/js/message-list-keyboard.js";

    [Test]
    public async Task Attach_ImportsTheModuleAndTakesAnAttachment()
    {
        var runtime = new FakeJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);

        await interop.AttachAsync(default);

        runtime.ImportedPaths.Should().Equal(ModuleUrl);
        runtime.Module.Identifiers.Should().Equal("attach");
        await interop.DetachAsync();
        runtime.Module.Identifiers.Should().Equal("attach", "detach");
    }

    [Test]
    public async Task Attach_Twice_KeepsTheFirstAttachment()
    {
        var runtime = new FakeJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);

        await interop.AttachAsync(default);
        await interop.AttachAsync(default);

        runtime.Module.Identifiers.Should().Equal("attach");
    }

    [Test]
    public async Task Detach_WithoutAnAttachment_DoesNothing()
    {
        var runtime = new FakeJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);

        await interop.DetachAsync();

        runtime.Module.Identifiers.Should().BeEmpty();
    }

    [Test]
    public async Task Detach_Twice_ReleasesTheAttachmentOnce()
    {
        var runtime = new FakeJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);
        await interop.AttachAsync(default);

        await interop.DetachAsync();
        await interop.DetachAsync();

        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.Arguments[1].Should().Equal(1);
    }

    [Test]
    public async Task Reveal_PassesTheListAndIndex()
    {
        var runtime = new FakeJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);
        var list = new ElementReference("message-list");

        await interop.RevealAsync(list, 7);

        runtime.Module.Identifiers.Should().Equal("reveal");
        runtime.Module.Arguments[0].Should().Equal(list, 7);
    }

    [Test]
    public async Task Reveal_ForNoRow_DoesNothing()
    {
        var runtime = new FakeJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);

        await interop.RevealAsync(default, -1);

        runtime.Module.Identifiers.Should().BeEmpty();
    }

    [Test]
    public async Task Attach_WhenTheRuntimeCannotImport_IsANoOp()
    {
        var runtime = new FakeJSRuntime { ModuleUnavailable = true };
        var interop = new MessageListKeyboardInterop(runtime);

        await interop.AttachAsync(default);
        await interop.DetachAsync();

        runtime.ImportedPaths.Should().Equal(ModuleUrl);
        runtime.Module.Identifiers.Should().BeEmpty();
    }

    [Test]
    public async Task Dispose_ReleasesTheListenerAndTheModuleOnce()
    {
        var runtime = new FakeJSRuntime();
        var interop = new MessageListKeyboardInterop(runtime);
        await interop.AttachAsync(default);

        await interop.DisposeAsync();
        await interop.DisposeAsync();
        await interop.AttachAsync(default);

        runtime.Module.Identifiers.Should().Equal("attach", "detach");
        runtime.Module.DisposeCount.Should().Be(1);
    }

    private sealed class FakeJSRuntime : IJSRuntime
    {
        internal FakeModule Module { get; } = new();

        internal List<string?> ImportedPaths { get; } = [];

        internal bool ModuleUnavailable { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import")
                return default;

            ImportedPaths.Add(args is { Length: > 0 } ? args[0] as string : null);
            if (ModuleUnavailable)
                return new ValueTask<TValue>(Task.FromResult(default(TValue)!));

            return new ValueTask<TValue>(Task.FromResult((TValue)(object)Module));
        }
    }

    private sealed class FakeModule : IJSObjectReference
    {
        private int _nextAttachment = 1;

        internal List<string> Identifiers { get; } = [];

        internal List<object?[]> Arguments { get; } = [];

        internal int DisposeCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Identifiers.Add(identifier);
            Arguments.Add(args ?? []);

            if (identifier == "attach")
                return new ValueTask<TValue>(Task.FromResult((TValue)(object)(int?)_nextAttachment++));

            return default;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return default;
        }
    }
}
