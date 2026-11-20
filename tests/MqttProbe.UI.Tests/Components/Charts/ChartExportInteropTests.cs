using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Charts;

[TestFixture]
public class ChartExportInteropTests
{
    [Test]
    public void ModulePath_IsTheVersionedExportModule()
    {
        ChartExportInterop.ModulePath.Should()
            .Be("./_content/MqttProbe.UI/js/chart-export.js?v=5");
    }

    [Test]
    public async Task CaptureAsync_ImportsTheModuleOnceAndMapsTheSnapshot()
    {
        var runtime = new FakeRuntime();
        var snapshot = Snapshot("export-1");
        runtime.Module.Handler = (_, _) => Task.FromResult<object?>(snapshot);
        await using var interop = new ChartExportInterop(runtime);

        var first = await interop.CaptureAsync("chart-1");
        var second = await interop.CaptureAsync("chart-1");

        runtime.Imports.Should().Be(1, "the module is imported lazily and reused");
        runtime.Module.Invoked("captureSnapshot").Should().Be(2);
        first.Should().Be(snapshot);
        second.Should().Be(snapshot);
    }

    [Test]
    public async Task CaptureAsync_ReturnsNullForAnUnavailableChart()
    {
        var runtime = new FakeRuntime();
        runtime.Module.Handler = (_, _) => Task.FromResult<object?>(null);
        var interop = new ChartExportInterop(runtime);

        var snapshot = await interop.CaptureAsync("missing");

        snapshot.Should().BeNull();
        await interop.DisposeAsync();
        runtime.Module.Invoked("releaseSnapshot").Should().Be(0,
            "a null capture has nothing to release");
        runtime.Module.Disposed.Should().Be(1);
    }

    [Test]
    public async Task CaptureAsync_PropagatesAFailureWithoutTrackingTheSnapshot()
    {
        var runtime = new FakeRuntime();
        runtime.Module.Handler = (identifier, _) => identifier == "captureSnapshot"
            ? Task.FromException<object?>(new JSException("capture failed"))
            : Task.FromResult<object?>(null);
        var interop = new ChartExportInterop(runtime);

        await Assert.ThrowsAsync<JSException>(() => interop.CaptureAsync("chart-1"));
        await interop.DisposeAsync();
        runtime.Module.Invoked("releaseSnapshot").Should().Be(0);
    }

    [Test]
    public async Task ExportAsync_SendsTheExactSnapshotSelectionFormatAndFileName()
    {
        var runtime = new FakeRuntime();
        runtime.Module.Handler = (identifier, _) => Task.FromResult<object?>(
            identifier == "captureSnapshot" ? Snapshot("export-1") : null);
        await using var interop = new ChartExportInterop(runtime);
        var snapshot = await interop.CaptureAsync("chart-1");

        await interop.ExportAsync(snapshot!.Id, ["serie-a", "serie-b"], "png", "chart.png");

        var call = runtime.Module.Invocations.Single(entry => entry.Identifier == "exportSnapshot");
        call.Args![0].Should().Be("export-1");
        call.Args[1].Should().BeEquivalentTo(new[] { "serie-a", "serie-b" });
        call.Args[2].Should().Be("png");
        call.Args[3].Should().Be("chart.png");
    }

    [Test]
    public async Task ExportAsync_PropagatesAFailureInsteadOfReportingSuccess()
    {
        var runtime = new FakeRuntime();
        runtime.Module.Handler = (identifier, _) => identifier == "exportSnapshot"
            ? Task.FromException<object?>(new JSException("image rendering failed"))
            : Task.FromResult<object?>(Snapshot("export-1"));
        await using var interop = new ChartExportInterop(runtime);
        var snapshot = await interop.CaptureAsync("chart-1");

        var export = () => interop.ExportAsync(snapshot!.Id, ["serie-a"], "svg", "chart.svg");

        await export.Should().ThrowAsync<JSException>();
    }

    [Test]
    public async Task ReleaseAsync_ReleasesEachCaptureOnlyOnce()
    {
        var runtime = new FakeRuntime();
        runtime.Module.Handler = (identifier, _) => Task.FromResult<object?>(
            identifier == "captureSnapshot" ? Snapshot("export-1") : null);
        await using var interop = new ChartExportInterop(runtime);
        var snapshot = await interop.CaptureAsync("chart-1");

        await interop.ReleaseAsync(snapshot!.Id);
        await interop.ReleaseAsync(snapshot.Id);

        runtime.Module.Invoked("releaseSnapshot").Should().Be(1);
        runtime.Module.Invocations.Single(entry => entry.Identifier == "releaseSnapshot")
            .Args![0].Should().Be("export-1");
    }

    [Test]
    public async Task DisposeAsync_WaitsForAnInFlightCaptureAndReleasesIt()
    {
        var runtime = new FakeRuntime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Module.Handler = async (identifier, _) =>
        {
            if (identifier != "captureSnapshot")
                return null;

            started.TrySetResult();
            await resume.Task;
            return Snapshot("export-1");
        };
        var interop = new ChartExportInterop(runtime);
        var capture = interop.CaptureAsync("chart-1");
        await started.Task;

        var dispose = interop.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse("the gate serializes an in-flight capture and disposal");

        resume.SetResult();
        var snapshot = await capture;
        await dispose;

        snapshot!.Id.Should().Be("export-1");
        runtime.Module.Invocations.Single(entry => entry.Identifier == "releaseSnapshot")
            .Args![0].Should().Be("export-1");
        runtime.Module.Disposed.Should().Be(1);
    }

    [Test]
    public async Task DisposeAsync_ToleratesADisconnectedCircuitWhileReleasing()
    {
        var runtime = new FakeRuntime();
        runtime.Module.Handler = (identifier, _) => identifier == "captureSnapshot"
            ? Task.FromResult<object?>(Snapshot("export-1"))
            : throw new JSDisconnectedException("circuit closed");
        var interop = new ChartExportInterop(runtime);
        await interop.CaptureAsync("chart-1");

        var dispose = () => interop.DisposeAsync().AsTask();

        await dispose.Should().NotThrowAsync();
        runtime.Module.Disposed.Should().Be(1);
    }

    [Test]
    public async Task AfterDisposal_CaptureIsNullAndExportReportsTheDisposedState()
    {
        var runtime = new FakeRuntime();
        runtime.Module.Handler = (_, _) => Task.FromResult<object?>(Snapshot("export-1"));
        var interop = new ChartExportInterop(runtime);
        await interop.DisposeAsync();

        (await interop.CaptureAsync("chart-1")).Should().BeNull();
        await interop.ReleaseAsync("export-1");
        var export = () => interop.ExportAsync("export-1", ["serie-a"], "csv", "chart.csv");

        await export.Should().ThrowAsync<ObjectDisposedException>();
        runtime.Imports.Should().Be(0);
    }

    [Test]
    public async Task CaptureAsync_PropagatesAModuleImportFailure()
    {
        var runtime = new FakeRuntime
        {
            ImportFailure = new JSException("Failed to fetch dynamically imported module"),
        };
        await using var interop = new ChartExportInterop(runtime);

        await Assert.ThrowsAsync<JSException>(() => interop.CaptureAsync("chart-1"));
    }

    private static ChartExportSnapshot Snapshot(string id) =>
        new(id, "local", "12h",
        [
            new ChartExportSeriesSummary("serie-a", "Temp", "#F97316", 2),
            new ChartExportSeriesSummary("serie-b", "Flow", "#A78BFA", 1),
        ]);

    private sealed class FakeRuntime : IJSRuntime
    {
        internal FakeModule Module { get; } = new();
        internal int Imports { get; private set; }
        internal Exception? ImportFailure { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier != "import")
                throw new InvalidOperationException($"Unexpected runtime identifier '{identifier}'.");

            Imports++;
            if (ImportFailure is not null)
                return ValueTask.FromException<TValue>(ImportFailure);

            args![0].Should().Be(ChartExportInterop.ModulePath);
            return ValueTask.FromResult((TValue)(object)Module);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class FakeModule : IJSObjectReference
    {
        internal List<(string Identifier, object?[]? Args)> Invocations { get; } = [];
        internal Func<string, object?[]?, Task<object?>>? Handler { get; set; }
        internal int Disposed { get; private set; }

        internal int Invoked(string identifier) =>
            Invocations.Count(entry => entry.Identifier == identifier);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Invocations.Add((identifier, args));
            var result = Handler is null ? null : await Handler(identifier, args);
            return identifier == "captureSnapshot" ? (TValue)result! : default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }
}
