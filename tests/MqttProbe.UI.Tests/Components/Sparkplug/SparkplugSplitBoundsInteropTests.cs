using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugSplitBoundsInteropTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DisposeWhileAttachIsPending_DetachesTheTokenBeforeDisposingTheModule()
    {
        var module = new FakeModule();
        var interop = new SparkplugSplitBoundsInterop(new FakeJsRuntime(module));

        (await interop.InstallAsync()).Should().BeTrue();
        var attach = interop.AttachAsync(new ElementReference("wrapper"));
        await module.AttachStarted.WaitAsync(_timeout);

        var dispose = interop.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse();

        module.CompleteAttach(7);
        await attach.WaitAsync(_timeout);
        await dispose.WaitAsync(_timeout);

        module.Invocations.Should().Equal("install", "attach", "detach:7", "dispose");
    }

    [Test]
    public async Task DisposeWhileImportIsPending_ReleasesTheModuleWithoutInstalling()
    {
        var module = new FakeModule();
        var js = new FakeJsRuntime(module);
        js.HoldImport();
        var interop = new SparkplugSplitBoundsInterop(js);

        var attach = interop.AttachAsync(new ElementReference("wrapper"));
        await js.ImportStarted.WaitAsync(_timeout);

        var dispose = interop.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse();

        js.ReleaseImport();
        await attach.WaitAsync(_timeout);
        await dispose.WaitAsync(_timeout);

        js.Imports.Should().Be(1);
        module.Invocations.Should().Equal("dispose");
    }

    [Test]
    public async Task DisposeAfterAttach_DetachesTheTokenAndDisposesTheModule()
    {
        var module = new FakeModule();
        module.CompleteAttach(3);
        var interop = new SparkplugSplitBoundsInterop(new FakeJsRuntime(module));

        (await interop.InstallAsync()).Should().BeTrue();
        await interop.AttachAsync(new ElementReference("wrapper"));
        await interop.DisposeAsync();

        module.Invocations.Should().Equal("install", "attach", "detach:3", "dispose");
    }

    [Test]
    public async Task DisposeWhileInstallIsPending_ReleasesTheModuleWithoutKeepingIt()
    {
        var module = new FakeModule();
        module.HoldInstall();
        var interop = new SparkplugSplitBoundsInterop(new FakeJsRuntime(module));

        var install = interop.InstallAsync();
        await module.InstallStarted.WaitAsync(_timeout);

        var dispose = interop.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse();

        module.ReleaseInstall();
        (await install.WaitAsync(_timeout)).Should().BeFalse();
        await dispose.WaitAsync(_timeout);

        module.Invocations.Should().Equal("install", "dispose");
    }

    [Test]
    public async Task AttachAfterDispose_DoesNotTouchJs()
    {
        var js = new FakeJsRuntime(new FakeModule());
        var interop = new SparkplugSplitBoundsInterop(js);
        await interop.DisposeAsync();

        (await interop.InstallAsync()).Should().BeFalse();
        await interop.AttachAsync(new ElementReference("wrapper"));

        js.Imports.Should().Be(0);
    }

    private sealed class FakeJsRuntime(FakeModule module) : IJSRuntime
    {
        private readonly TaskCompletionSource _importGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holdImport;

        internal Task ImportStarted => _importStarted.Task;
        internal int Imports { get; private set; }

        private readonly TaskCompletionSource _importStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void HoldImport() => _holdImport = true;

        internal void ReleaseImport() => _importGate.TrySetResult();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier != "import")
                throw new InvalidOperationException($"Unexpected JS invocation: {identifier}");

            Imports++;
            _importStarted.TrySetResult();
            if (_holdImport)
                await _importGate.Task;

            return (TValue)(object)module;
        }
    }

    private sealed class FakeModule : IJSObjectReference
    {
        private readonly List<string> _invocations = [];
        private readonly TaskCompletionSource<int?> _attach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _attachStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _installGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _installStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _holdInstall;
        private bool _disposed;

        internal IReadOnlyList<string> Invocations => _invocations;
        internal Task AttachStarted => _attachStarted.Task;
        internal Task InstallStarted => _installStarted.Task;

        internal void CompleteAttach(int? token) => _attach.TrySetResult(token);

        internal void HoldInstall() => _holdInstall = true;

        internal void ReleaseInstall() => _installGate.TrySetResult();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FakeModule));

            switch (identifier)
            {
                case "install":
                    _invocations.Add("install");
                    _installStarted.TrySetResult();
                    return AwaitInstallAsync<TValue>();
                case "attach":
                    _invocations.Add("attach");
                    _attachStarted.TrySetResult();
                    return AwaitAttachAsync<TValue>();
                case "detach":
                    _invocations.Add($"detach:{args![0]}");
                    return ValueTask.FromResult(default(TValue)!);
                default:
                    throw new InvalidOperationException($"Unexpected JS invocation: {identifier}");
            }
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _invocations.Add("dispose");
            }

            return ValueTask.CompletedTask;
        }

        private async ValueTask<TValue> AwaitAttachAsync<TValue>()
        {
            var token = await _attach.Task;
            return token is null ? default! : (TValue)(object)token;
        }

        private async ValueTask<TValue> AwaitInstallAsync<TValue>()
        {
            if (_holdInstall)
                await _installGate.Task;

            return default!;
        }
    }
}
