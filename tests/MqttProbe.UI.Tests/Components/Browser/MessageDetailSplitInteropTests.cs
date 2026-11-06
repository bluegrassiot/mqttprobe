using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Browser;

[TestFixture]
public class MessageDetailSplitInteropTests
{
    private const string ModuleUrl = "./_content/MqttProbe.UI/js/message-detail-split.js";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DisposeWhileImportIsPending_DisposesTheModuleWithoutFitting()
    {
        var runtime = new GatedImportRuntime();
        var interop = new MessageDetailSplitInterop(runtime);

        var fit = interop.FitToContentAsync(new ElementReference("meta-pane"));
        await runtime.ImportStarted.WaitAsync(_timeout);

        var dispose = interop.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse();

        runtime.ReleaseImport();
        (await fit.WaitAsync(_timeout)).Should().BeFalse();
        await dispose.WaitAsync(_timeout);

        runtime.Module.Identifiers.Should().BeEmpty();
        runtime.Module.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task Fit_ImportsTheModuleOnceAndDisposesItOnDispose()
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = new MessageDetailSplitInterop(runtime);
        var pane = new ElementReference("meta-pane");

        await interop.FitToContentAsync(pane);
        await interop.FitToContentAsync(pane);
        await interop.DisposeAsync();

        runtime.ImportedPaths.Should().Equal(ModuleUrl);
        runtime.Module.Identifiers.Should().Equal("fitToContent", "fitToContent");
        runtime.Module.Arguments[0].Should().Equal(pane);
        runtime.Module.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task FitAfterDispose_DoesNotTouchJs()
    {
        var runtime = new FakeKeyboardJSRuntime();
        var interop = new MessageDetailSplitInterop(runtime);

        await interop.DisposeAsync();

        (await interop.FitToContentAsync(default)).Should().BeFalse();

        runtime.ImportedPaths.Should().BeEmpty();
    }

    private sealed class GatedImportRuntime : IJSRuntime
    {
        private readonly TaskCompletionSource _importGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _importStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakeKeyboardModule Module { get; } = new();

        internal Task ImportStarted => _importStarted.Task;

        internal void ReleaseImport() => _importGate.TrySetResult();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import")
                throw new InvalidOperationException($"Unexpected JS invocation: {identifier}");

            _importStarted.TrySetResult();
            await _importGate.Task;
            return (TValue)(object)Module;
        }
    }
}
