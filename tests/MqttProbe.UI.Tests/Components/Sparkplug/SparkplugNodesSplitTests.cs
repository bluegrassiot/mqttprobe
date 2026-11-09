using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.Core.Models.Sparkplug;
using MqttProbe.Core.Services.Configuration;
using MqttProbe.Core.Services.Sparkplug;
using MqttProbe.UI.Tests.TestHelpers;
using MudBlazor;

namespace MqttProbe.UI.Tests.Components.Sparkplug;

[TestFixture]
public class SparkplugNodesSplitTests : BunitTestContext
{
    private readonly GatedSplitModule _gatedModule = new();
    private GatedSplitJsRuntime _gatedRuntime = null!;

    [SetUp]
    public void Setup()
    {
        var topology = Substitute.For<ISparkplugTopologyService>();
        topology.Groups.Returns(new Dictionary<string, SpbGroup>());
        Services.AddSingleton(topology);
        Services.AddSingleton(Substitute.For<ISparkplugCommandService>());

        var sparkplugSettings = Substitute.For<ISparkplugSettings>();
        sparkplugSettings.Sparkplug.Returns(new SparkplugSettings());
        Services.AddSparkplugSettings(sparkplugSettings);

        Services.AddSingleton(Substitute.For<IDialogService>());
        Services.AddSingleton(Substitute.For<ISnackbar>());
        _gatedRuntime = new GatedSplitJsRuntime(JSInterop.JSRuntime, _gatedModule);
        Services.AddSingleton<IJSRuntime>(_gatedRuntime);
        Services.AddUiSettings(SettingsSubstituteRegistration.CreateUiSettings());
        EnsureMudProviders();
    }

    [Test]
    public void DesktopSplit_RendersTheSharedSplitterWithBothPanes()
    {
        var cut = RenderNodes(Breakpoint.Lg);

        var panel = cut.Find(".spb-split-panel");
        panel.ClassList.Should().Contain("app-split-panel");

        var divider = panel.QuerySelector(".divider")!;
        divider.GetAttribute("role").Should().Be("separator");
        divider.GetAttribute("aria-label").Should().Be("Resize panels");
        divider.GetAttribute("aria-orientation").Should().Be("vertical");
        divider.GetAttribute("tabindex").Should().Be("0");

        panel.QuerySelectorAll(":scope > .child-panel").Should().HaveCount(2);
        panel.QuerySelector(".spb-panel-nodes > .spb-split__nodes").Should().NotBeNull();
        panel.QuerySelectorAll(".spb-split__detail").Should().ContainSingle();
        cut.FindAll(".spb-split-wrap").Should().ContainSingle();
    }

    [Test]
    public void DesktopSplit_AppliesBoundedDragInteropToTheNodesWrapper()
    {
        var module = JSInterop.SetupModule("./_content/MqttProbe.UI/js/sparkplug-nodes-split-bounds.js?v=2");
        module.SetupVoid("install", _ => true).SetVoidResult();
        module.Setup<int?>("attach", _ => true).SetResult(1);

        var cut = RenderNodes(Breakpoint.Lg);

        cut.WaitForAssertion(() => module.VerifyInvoke("attach"));
        module.VerifyInvoke("install", 1);
        module.VerifyInvoke("attach", 1);
    }

    [Test]
    public void DesktopSplit_SizesThroughTheGuardedModuleAndNeverCallsMudSplitPanelGlobals()
    {
        var module = SetupSplitModule();
        module.Setup<bool>("ensureSized", _ => true).SetResult(true);

        var cut = RenderNodes(Breakpoint.Lg);

        cut.WaitForAssertion(() => module.VerifyInvoke("ensureSized", 1));
        JSInterop.Invocations.Identifiers.Should().NotContain("mudSplitPanel_getDividerPosition");
        JSInterop.Invocations.Identifiers.Should().NotContain("mudSplitPanel_resetDividerPosition");
    }

    [Test]
    public void DesktopSplit_SizesOnlyOnceAfterTheModuleReportsSuccess()
    {
        var module = SetupSplitModule();
        module.Setup<bool>("ensureSized", _ => true).SetResult(true);

        var cut = RenderNodes(Breakpoint.Lg);

        cut.WaitForAssertion(() => module.VerifyInvoke("ensureSized", 1));
        cut.Render();
        module.VerifyInvoke("ensureSized", 1);
    }

    [Test]
    public void DesktopSplit_RetriesSizingUntilTheModuleReportsSuccess()
    {
        var module = SetupSplitModule();
        var ensure = module.Setup<bool>("ensureSized", _ => true).SetResult(false);

        var cut = RenderNodes(Breakpoint.Lg);
        cut.WaitForAssertion(() => module.VerifyInvoke("ensureSized", 1));

        ensure.SetResult(true);
        var before = JSInterop.Invocations["ensureSized"].Count;
        cut.Render();
        cut.WaitForAssertion(() =>
            JSInterop.Invocations["ensureSized"].Count.Should().BeGreaterThan(before));

        var sized = JSInterop.Invocations["ensureSized"].Count;
        cut.Render();
        JSInterop.Invocations["ensureSized"].Count.Should().Be(sized);
    }

    [Test]
    public void DesktopSplit_WaitsForAttachmentBeforeSizingAndDoesNotResetAgain()
    {
        var module = SetupSplitModule();
        var attach = module.Setup<int?>("attach", _ => true).SetResult(null);
        module.Setup<bool>("ensureSized", _ => true).SetResult(true);

        var cut = RenderNodes(Breakpoint.Lg);

        cut.WaitForAssertion(() => module.VerifyInvoke("attach", 1));
        JSInterop.Invocations.Identifiers.Should().NotContain("ensureSized");

        attach.SetResult(1);
        cut.Render();
        cut.WaitForAssertion(() => module.VerifyInvoke("ensureSized", 1));

        cut.Render();
        module.VerifyInvoke("ensureSized", 1);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task DesktopSplit_RemountDuringPendingAttachment_InitializesReplacementOnly()
    {
        var module = _gatedModule;
        _gatedRuntime.Enabled = true;
        try
        {
            var host = Render<BreakpointHost>();
            var cut = host.FindComponent<SparkplugNodesView>();

            host.WaitForAssertion(() => module.AttachCount.Should().Be(1));

            await host.InvokeAsync(() => host.Instance.Set(Breakpoint.Sm));
            host.WaitForAssertion(() => host.FindAll(".mud-split-panel").Should().BeEmpty());
            cut.Render();

            await host.InvokeAsync(() => host.Instance.Set(Breakpoint.Lg));
            host.WaitForAssertion(() => host.FindAll(".mud-split-panel").Should().ContainSingle());
            cut.Render();
            module.EnsureCount.Should().Be(0);

            module.ReleaseAttach(1);

            await module.SecondAttachStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await module.EnsureSizedInvoked.WaitAsync(TimeSpan.FromSeconds(5));

            module.AttachCount.Should().BeGreaterThanOrEqualTo(2);
            module.EnsureCount.Should().Be(1);

            await host.InvokeAsync(() => host.Instance.Set(Breakpoint.Lg));
            module.EnsureCount.Should().Be(1);
        }
        finally
        {
            module.ReleasePendingOperations();
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task DesktopSplit_OverlappingRenderDuringPendingAttachment_AttachesAndSizesOnce()
    {
        var module = _gatedModule;
        _gatedRuntime.Enabled = true;
        try
        {
            var (cut, view) = RenderTrackedNodes();

            cut.WaitForAssertion(() => module.AttachCount.Should().Be(1));
            view.StartedCount.Should().Be(1);

            cut.Render();
            cut.WaitForAssertion(() => view.StartedCount.Should().Be(2));

            module.ReleaseAttach(1);

            await Task.WhenAll(view.Renders);

            module.AttachCount.Should().Be(1);
            module.EnsureCount.Should().Be(1);

            cut.Render();
            await Task.WhenAll(view.Renders);

            module.AttachCount.Should().Be(1);
            module.EnsureCount.Should().Be(1);
        }
        finally
        {
            module.ReleasePendingOperations();
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task DesktopSplit_OverlappingRenderDuringPendingSizing_DoesNotQueueAnotherSizing()
    {
        var module = _gatedModule;
        _gatedRuntime.Enabled = true;
        try
        {
            module.SetEnsureResult(false);
            var (cut, view) = RenderTrackedNodes();

            module.ReleaseAttach(1);
            await module.EnsureSizedInvoked.WaitAsync(TimeSpan.FromSeconds(5));
            module.EnsureCount.Should().Be(1);
            module.AttachCount.Should().Be(1);

            module.HoldNextEnsure();
            cut.Render();
            cut.WaitForAssertion(() => module.EnsureCount.Should().Be(2));
            cut.WaitForAssertion(() => view.StartedCount.Should().Be(2));

            cut.Render();
            cut.WaitForAssertion(() => view.StartedCount.Should().Be(3));
            module.EnsureCount.Should().Be(2);

            module.ReleaseEnsure(true);

            await Task.WhenAll(view.Renders);

            module.AttachCount.Should().Be(1);
            module.EnsureCount.Should().Be(2);

            cut.Render();
            await Task.WhenAll(view.Renders);
            module.EnsureCount.Should().Be(2);
        }
        finally
        {
            module.ReleasePendingOperations();
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task DesktopSplit_AbandonedPendingAttach_CleanupAllowsDisposal()
    {
        var module = _gatedModule;
        _gatedRuntime.Enabled = true;

        try
        {
            var cut = RenderNodes(Breakpoint.Lg);

            cut.WaitForAssertion(() => module.AttachCount.Should().Be(1));

            module.ReleasePendingOperations();
            module.AttachReleased.Should().BeTrue();
            module.AttachResult.Should().BeNull();

            await DisposeComponentsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            module.ReleasePendingOperations();
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task DesktopSplit_AbandonedPendingSizing_CleanupAllowsDisposal()
    {
        var module = _gatedModule;
        _gatedRuntime.Enabled = true;
        module.HoldNextEnsure();

        try
        {
            var cut = RenderNodes(Breakpoint.Lg);

            cut.WaitForAssertion(() => module.AttachCount.Should().Be(1));

            module.ReleaseAttach(1);
            await module.EnsureSizedInvoked.WaitAsync(TimeSpan.FromSeconds(5));
            module.EnsureCount.Should().Be(1);

            module.ReleasePendingOperations();
            module.PendingEnsureResult.Should().Be(false);

            await DisposeComponentsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            module.ReleasePendingOperations();
        }
    }

    [Test]
    public void DesktopSplit_WhenTheLegacyMudGetterThrows_DoesNotInvokeItOrRenderFallback()
    {
        var module = SetupSplitModule();
        module.Setup<bool>("ensureSized", _ => true).SetResult(false);
        JSInterop.Setup<double>("mudSplitPanel_getDividerPosition", _ => true)
            .SetException(new JSException("split panel runtime missing"));

        var host = Render<BreakpointHost>(parameters => parameters
            .Add(host => host.WrapInErrorBoundary, true));
        var cut = host.FindComponent<SparkplugNodesView>();

        cut.WaitForAssertion(() => module.VerifyInvoke("ensureSized", 1));

        host.FindAll(".blazor-error-boundary").Should().BeEmpty();
        JSInterop.Invocations.Identifiers.Should().NotContain("mudSplitPanel_getDividerPosition");
    }

    private Bunit.BunitJSModuleInterop SetupSplitModule()
    {
        var module = JSInterop.SetupModule("./_content/MqttProbe.UI/js/sparkplug-nodes-split-bounds.js?v=2");
        module.SetupVoid("install", _ => true).SetVoidResult();
        module.Setup<int?>("attach", _ => true).SetResult(1);
        return module;
    }

    [Test]
    public void StackedLayout_KeepsThePlainSplitWithoutASeparator()
    {
        var cut = Render<SparkplugNodesView>();

        cut.Find(".spb-split").Should().NotBeNull();
        cut.FindAll(".mud-split-panel").Should().BeEmpty();
        cut.FindAll(".divider").Should().BeEmpty();
    }

    [TestCase(Breakpoint.Xs, false)]
    [TestCase(Breakpoint.Sm, false)]
    [TestCase(Breakpoint.Md, true)]
    [TestCase(Breakpoint.Lg, true)]
    public void ResponsiveBreakpoints_RenderOnlyTheMatchingLayout(Breakpoint breakpoint, bool desktop)
    {
        var cut = RenderNodes(breakpoint);

        var desktopRoots = cut.FindAll(".spb-split-wrap");
        var stackedRoots = cut.FindAll(".spb-split");

        if (desktop)
        {
            desktopRoots.Should().ContainSingle();
            stackedRoots.Should().BeEmpty();
        }
        else
        {
            stackedRoots.Should().ContainSingle();
            desktopRoots.Should().BeEmpty();
        }
    }

    private IRenderedComponent<SparkplugNodesView> RenderNodes(Breakpoint breakpoint)
    {
        var wrapper = Render<CascadingValue<Breakpoint>>(parameters => parameters
            .Add(value => value.Value, breakpoint)
            .Add(value => value.IsFixed, true)
            .Add(value => value.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<SparkplugNodesView>(0);
                builder.CloseComponent();
            })));

        return wrapper.FindComponent<SparkplugNodesView>();
    }

    private (IRenderedComponent<TrackedNodesView> Cut, TrackedNodesView View) RenderTrackedNodes()
    {
        var wrapper = Render<CascadingValue<Breakpoint>>(parameters => parameters
            .Add(value => value.Value, Breakpoint.Lg)
            .Add(value => value.IsFixed, true)
            .Add(value => value.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<TrackedNodesView>(0);
                builder.CloseComponent();
            })));

        var cut = wrapper.FindComponent<TrackedNodesView>();
        return (cut, cut.Instance);
    }

    private sealed class GatedSplitJsRuntime(IJSRuntime inner, GatedSplitModule module) : IJSRuntime
    {
        public bool Enabled { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => Enabled && identifier == "import"
                ? ValueTask.FromResult((TValue)(object)module)
                : inner.InvokeAsync<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
            => Enabled && identifier == "import"
                ? ValueTask.FromResult((TValue)(object)module)
                : inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
    }

    private sealed class GatedSplitModule : IJSObjectReference
    {
        private readonly Lock _sync = new();
        private readonly List<string> _invocations = [];
        private readonly TaskCompletionSource<int?> _attach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondAttachStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ensureSizedInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? _ensureGate;
        private int _attachCount;
        private bool _ensureResult = true;
        private bool? _pendingEnsureResult;
        private int? _releasedAttachToken;
        private bool _attachReleased;

        public int AttachCount
        {
            get { lock (_sync) return _attachCount; }
        }

        public Task SecondAttachStarted => _secondAttachStarted.Task;

        public int? AttachResult
        {
            get { lock (_sync) return _releasedAttachToken; }
        }

        public bool AttachReleased
        {
            get { lock (_sync) return _attachReleased; }
        }

        public int EnsureCount
        {
            get { lock (_sync) return _invocations.Count(invocation => invocation == "ensureSized"); }
        }

        public Task EnsureSizedInvoked => _ensureSizedInvoked.Task;

        public bool? PendingEnsureResult
        {
            get { lock (_sync) return _pendingEnsureResult; }
        }

        public void SetEnsureResult(bool result)
        {
            lock (_sync) _ensureResult = result;
        }

        public void ReleaseAttach(int? token)
        {
            if (!_attach.TrySetResult(token))
                return;

            lock (_sync)
            {
                _releasedAttachToken = token;
                _attachReleased = true;
            }
        }

        public void ReleasePendingOperations()
        {
            if (_attach.TrySetResult(null))
            {
                lock (_sync)
                {
                    _releasedAttachToken = null;
                    _attachReleased = true;
                }
            }

            TaskCompletionSource<bool>? gate;
            lock (_sync)
            {
                _pendingEnsureResult = false;
                gate = _ensureGate;
                _ensureGate = null;
            }

            gate?.TrySetResult(false);
        }

        public void HoldNextEnsure()
        {
            lock (_sync)
                _ensureGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseEnsure(bool result)
        {
            TaskCompletionSource<bool>? gate;
            lock (_sync)
            {
                _ensureResult = result;
                _pendingEnsureResult = result;
                gate = _ensureGate;
                _ensureGate = null;
            }

            gate?.TrySetResult(result);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            switch (identifier)
            {
                case "install":
                    Record("install");
                    return ValueTask.FromResult(default(TValue)!);
                case "attach":
                    Record("attach");
                    return AttachAsync<TValue>();
                case "ensureSized":
                    return EnsureSizedAsync<TValue>();
                case "detach":
                    Record($"detach:{args![0]}");
                    return ValueTask.FromResult(default(TValue)!);
                default:
                    throw new InvalidOperationException($"Unexpected JS invocation: {identifier}");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ValueTask<TValue> AttachAsync<TValue>()
        {
            int count;
            bool secondAttach;
            lock (_sync)
            {
                count = ++_attachCount;
                secondAttach = count == 2;
            }

            if (secondAttach)
                _secondAttachStarted.TrySetResult();

            return count == 1
                ? AwaitAttachAsync<TValue>()
                : ValueTask.FromResult((TValue)(object)(int?)1);
        }

        private ValueTask<TValue> EnsureSizedAsync<TValue>()
        {
            TaskCompletionSource<bool>? gate;
            bool result;
            lock (_sync)
            {
                _invocations.Add("ensureSized");
                gate = _ensureGate;
                result = _ensureResult;
            }

            _ensureSizedInvoked.TrySetResult();

            return gate is null
                ? ValueTask.FromResult((TValue)(object)result)
                : AwaitEnsureAsync<TValue>(gate);
        }

        private void Record(string invocation)
        {
            lock (_sync) _invocations.Add(invocation);
        }

        private async ValueTask<TValue> AwaitAttachAsync<TValue>()
        {
            var token = await _attach.Task;
            return token is null ? default! : (TValue)(object)token;
        }

        private static async ValueTask<TValue> AwaitEnsureAsync<TValue>(TaskCompletionSource<bool> gate)
        {
            var result = await gate.Task;
            return (TValue)(object)result;
        }
    }

    public sealed class TrackedNodesView : SparkplugNodesView
    {
        private readonly Lock _sync = new();
        private readonly List<Task> _renders = [];

        public int StartedCount
        {
            get { lock (_sync) return _renders.Count; }
        }

        public Task[] Renders
        {
            get { lock (_sync) return [.. _renders]; }
        }

        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            var task = base.OnAfterRenderAsync(firstRender);
            lock (_sync) _renders.Add(task);
            return task;
        }
    }

    private sealed class BreakpointHost : ComponentBase
    {
        public Breakpoint Value { get; private set; } = Breakpoint.Lg;

        [Parameter]
        public bool WrapInErrorBoundary { get; set; }

        public void Set(Breakpoint value)
        {
            Value = value;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Breakpoint>>(0);
            builder.AddAttribute(1, "Value", Value);
            builder.AddAttribute(3, "ChildContent", (RenderFragment)(child =>
            {
                if (WrapInErrorBoundary)
                {
                    child.OpenComponent<ErrorBoundary>(0);
                    child.AddAttribute(1, "ChildContent", (RenderFragment)(inner =>
                    {
                        inner.OpenComponent<SparkplugNodesView>(0);
                        inner.CloseComponent();
                    }));
                    child.CloseComponent();
                    return;
                }

                child.OpenComponent<SparkplugNodesView>(0);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
