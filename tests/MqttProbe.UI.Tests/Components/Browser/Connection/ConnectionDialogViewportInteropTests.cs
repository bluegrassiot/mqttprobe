using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MqttProbe.UI.Components.Browser.Connection;

namespace MqttProbe.UI.Tests.Components.Browser;

// The import is completed by hand: a dialog can close mid-import, and every proxy for one URL shares the module's state.
[TestFixture]
public class ConnectionDialogViewportInteropTests
{
    private const string ModuleUrl = "./_content/MqttProbe.UI/js/connection-dialog-viewport.js";

    [Test]
    public async Task Attach_ImportsTheModuleAndTakesAnAttachment()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        runtime.Waiting.Should().BeTrue("the module import must be requested");
        var module = runtime.CompleteImport();
        await attach;

        runtime.ImportedIdentifiers.Should().Equal("import");
        runtime.ImportedPaths.Should().Equal(ModuleUrl);
        runtime.State.Invocations.Should().Equal("attach");
        runtime.State.Active.Should().HaveCount(1);
        module.Disposed.Should().BeFalse();
    }

    [Test]
    public async Task DisposeWhileTheImportIsInFlight_ReleasesTheModuleWithoutDetaching()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        await interop.DisposeAsync();
        var module = runtime.CompleteImport();
        await attach;

        runtime.State.Invocations.Should().BeEmpty(
            "a module that never attached must not run detach: the module is shared, so that would release another dialog's attachment");
        runtime.State.Active.Should().BeEmpty();
        module.Disposed.Should().BeTrue("the module that arrived late must not be leaked");
    }

    [Test]
    public async Task DisposeAfterAttach_DetachesOnlyItsOwnAttachment()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        var module = runtime.CompleteImport();
        await attach;
        var attachment = runtime.State.Active.Single();

        await interop.DisposeAsync();

        runtime.State.Invocations.Should().Equal("attach", "detach");
        runtime.State.Detached.Should().Equal(attachment);
        module.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task ClosingOneDialog_LeavesAnotherDialogsAttachmentAlone()
    {
        var runtime = new FakeJSRuntime();
        var closing = new ConnectionDialogViewportInterop(runtime);
        var opening = new ConnectionDialogViewportInterop(runtime);

        var closingAttach = closing.AttachAsync(default);
        var openingAttach = opening.AttachAsync(default);
        var closingModule = runtime.CompleteImport();
        var openingModule = runtime.CompleteImport();
        await Task.WhenAll(closingAttach, openingAttach);
        runtime.State.Active.Should().HaveCount(2);

        await closing.DisposeAsync();

        runtime.State.Active.Should().HaveCount(1,
            "only the closing dialog's attachment may be released");
        runtime.State.Detached.Should().HaveCount(1);
        closingModule.Disposed.Should().BeTrue();
        openingModule.Disposed.Should().BeFalse("the open dialog still needs the module");

        await opening.DisposeAsync();
        runtime.State.Active.Should().BeEmpty();
    }

    [Test]
    public async Task Attach_WhenTheRuntimeCannotImport_IsANoOp()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        runtime.CompleteImportWithoutModule();
        await attach;

        await interop.DisposeAsync();

        runtime.State.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task DisposeTwice_ReleasesTheAttachmentOnce()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        var module = runtime.CompleteImport();
        await attach;
        var attachment = runtime.State.Active.Single();

        await interop.DisposeAsync();
        await interop.DisposeAsync();

        runtime.State.Detached.Should().Equal(attachment);
        module.Disposed.Should().BeTrue();
    }

    [Test]
    public async Task DisposeWhileTheAttachIsInFlight_DetachesThatTokenAndReleasesTheModule()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        var module = runtime.CompleteImport(delayAttach: true);

        module.AttachWaiting.Should().BeTrue();
        runtime.State.Active.Should().HaveCount(1);

        await interop.DisposeAsync();

        module.DisposeCount.Should().Be(0,
            "the module is still owned by the in-flight attach, so nothing may be released yet");
        runtime.State.Active.Should().HaveCount(1);

        var attachment = module.CompleteAttach();
        await attach;

        runtime.State.Detached.Should().Equal([attachment],
            "the token that arrived after the dialog closed must be released");
        runtime.State.Active.Should().BeEmpty();
        module.DisposeCount.Should().Be(1, "the module is released exactly once");
    }

    [Test]
    public async Task AttachFailure_ReleasesTheModuleOnceAndKeepsTheFailure()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        var module = runtime.CompleteImport(failAttach: true);

        Assert.That(async () => await attach, Throws.TypeOf<JSException>());
        runtime.State.Invocations.Should().NotContain("detach",
            "nothing was registered, so there is no attachment to release");
        runtime.State.Active.Should().BeEmpty();
        module.DisposeCount.Should().Be(1, "the failed module reference must not be leaked");

        await interop.DisposeAsync();
        module.DisposeCount.Should().Be(1, "and it must not be released twice");
    }

    [Test]
    public async Task RememberAndRestoreFocus_UseTheDialogsModuleAndContentElement()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        runtime.CompleteImport();
        await attach;

        var content = new ElementReference("dialog-content");
        await interop.RememberFocusAsync(content);
        var restored = await interop.RestoreFocusAsync(content);

        restored.Should().BeFalse("the recording module reports that no previous focus was restored");
        runtime.State.Invocations.Should().Equal("attach", "rememberFocus", "restoreFocus");
        runtime.State.FocusArguments.Should().Equal(content, content);
    }

    [Test]
    public async Task RememberAndRestoreFocus_WithoutAModule_AreANoOp()
    {
        var runtime = new FakeJSRuntime();
        var interop = new ConnectionDialogViewportInterop(runtime);

        var attach = interop.AttachAsync(default);
        runtime.CompleteImportWithoutModule();
        await attach;

        await interop.RememberFocusAsync(default);
        var restored = await interop.RestoreFocusAsync(default);

        restored.Should().BeFalse();
        runtime.State.Invocations.Should().BeEmpty();
    }

    private sealed class FakeJSRuntime : IJSRuntime
    {
        private readonly Queue<TaskCompletionSource<IJSObjectReference?>> _pending = new();

        internal FakeModuleState State { get; } = new();

        internal List<string> ImportedIdentifiers { get; } = [];

        internal List<string?> ImportedPaths { get; } = [];

        internal bool Waiting => _pending.Count > 0;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                ImportedIdentifiers.Add(identifier);
                ImportedPaths.Add(args is { Length: > 0 } ? args[0] as string : null);
            }

            var pending = new TaskCompletionSource<IJSObjectReference?>();
            _pending.Enqueue(pending);
            return new ValueTask<TValue>((Task<TValue>)(object)pending.Task);
        }

        internal RecordingModule CompleteImport(bool delayAttach = false, bool failAttach = false)
        {
            var module = new RecordingModule(State) { DelayAttach = delayAttach, FailAttach = failAttach };
            _pending.Dequeue().SetResult(module);
            return module;
        }

        internal void CompleteImportWithoutModule() => _pending.Dequeue().SetResult(null);
    }

    private sealed class FakeModuleState
    {
        private int _nextAttachment = 1;

        internal List<string> Invocations { get; } = [];

        internal HashSet<int> Active { get; } = [];

        internal List<int> Detached { get; } = [];

        internal List<ElementReference> FocusArguments { get; } = [];

        internal int Attach()
        {
            Invocations.Add("attach");
            var attachment = _nextAttachment++;
            Active.Add(attachment);
            return attachment;
        }

        internal void Detach(int attachment)
        {
            Invocations.Add("detach");
            Detached.Add(attachment);
            Active.Remove(attachment);
        }
    }

    private sealed class RecordingModule(FakeModuleState state) : IJSObjectReference
    {
        private readonly Queue<(TaskCompletionSource<int?> Pending, int Attachment)> _pendingAttaches = new();

        internal bool DelayAttach { get; init; }

        internal bool FailAttach { get; init; }

        internal bool AttachWaiting => _pendingAttaches.Count > 0;

        internal int DisposeCount { get; private set; }

        internal bool Disposed => DisposeCount > 0;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "attach")
            {
                if (FailAttach)
                    throw new JSException("attach failed");

                var attachment = state.Attach();

                if (DelayAttach)
                {
                    var pending = new TaskCompletionSource<int?>();
                    _pendingAttaches.Enqueue((pending, attachment));
                    return new ValueTask<TValue>((Task<TValue>)(object)pending.Task);
                }

                return new ValueTask<TValue>((TValue)(object)(int?)attachment);
            }

            if (identifier == "detach" && args is { Length: > 0 } && args[0] is int token)
                state.Detach(token);

            if (identifier is "rememberFocus" or "restoreFocus")
            {
                state.Invocations.Add(identifier);
                state.FocusArguments.Add((ElementReference)args![0]!);
            }

            return default;
        }

        internal int CompleteAttach()
        {
            var (pending, attachment) = _pendingAttaches.Dequeue();
            pending.SetResult(attachment);
            return attachment;
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
