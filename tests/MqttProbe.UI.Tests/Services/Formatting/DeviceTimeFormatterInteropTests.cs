using System.Collections;
using Microsoft.JSInterop;
using MqttProbe.Core.Models.Configuration;
using MqttProbe.UI.Services.Formatting;

namespace MqttProbe.UI.Tests.Services.Formatting;

[TestFixture]
public class DeviceTimeFormatterInteropTests
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/message-time-format.js";

    [Test]
    public async Task FormatAsync_ImportsOnceAndPassesTheBatchWithExplicitPreferences()
    {
        var runtime = new FakeRuntime();
        await using var interop = new DeviceTimeFormatterInterop(runtime);
        var items = new List<DeviceTimeFormatRequest>
        {
            new(Guid.NewGuid(), 1_700_000_000_123, Detail: true)
        };
        var preferences = new MessageTimePreferences
        {
            Zone = MessageTimePreferences.ZoneLocal,
            ClockFormat = MessageTimePreferences.ClockFormat12Hour
        };

        var result = await interop.FormatAsync(items, preferences);

        runtime.ImportedPaths.Should().Equal(ModulePath);
        runtime.Module.Identifiers.Should().Equal("formatInstants");
        runtime.Module.Arguments[0][0].Should().BeSameAs(items);
        runtime.Module.Arguments[0][1].Should().BeEquivalentTo(new { zone = "local", clockFormat = "12h" });
        result.Should().BeSameAs(runtime.Module.Result);
    }

    [Test]
    public async Task FormatAsync_EmptyBatchDoesNotImportTheModule()
    {
        var runtime = new FakeRuntime();
        await using var interop = new DeviceTimeFormatterInterop(runtime);

        var result = await interop.FormatAsync([], new MessageTimePreferences());

        result.Should().BeNull();
        runtime.ImportedPaths.Should().BeEmpty();
    }

    [Test]
    public async Task EnsureBridgeAsync_InstallsTheGlobalBridgeFromTheSameModule()
    {
        var runtime = new FakeRuntime();
        await using var interop = new DeviceTimeFormatterInterop(runtime);

        (await interop.EnsureBridgeAsync()).Should().BeTrue();
        (await interop.EnsureBridgeAsync()).Should().BeTrue();

        runtime.ImportedPaths.Should().Equal(ModulePath);
        runtime.Module.Identifiers.Should().Equal("installTimeBridge", "installTimeBridge");
    }

    [Test]
    public async Task EnsureBridgeAsync_ConcurrentCallsShareOneModuleImport()
    {
        var runtime = new FakeRuntime();
        await using var interop = new DeviceTimeFormatterInterop(runtime);

        await Task.WhenAll(interop.EnsureBridgeAsync(), interop.EnsureBridgeAsync());

        runtime.ImportedPaths.Should().ContainSingle();
        runtime.Module.Identifiers.Should().Equal("installTimeBridge", "installTimeBridge");
    }

    [Test]
    public async Task FormatAsync_RetriesTheImportAfterTheFirstOneFails()
    {
        var runtime = new FakeRuntime();
        runtime.ImportFailures.Enqueue(new JSException("import failed"));
        await using var interop = new DeviceTimeFormatterInterop(runtime);
        var items = new List<DeviceTimeFormatRequest>
        {
            new(Guid.NewGuid(), 1_700_000_000_123, Detail: false)
        };
        var preferences = new MessageTimePreferences();

        await FluentActions.Awaiting(() => interop.FormatAsync(items, preferences))
            .Should().ThrowAsync<JSException>();

        (await interop.FormatAsync(items, preferences)).Should().BeSameAs(runtime.Module.Result);
        runtime.ImportedPaths.Should().Equal(ModulePath, ModulePath);
    }

    [Test]
    public async Task EnsureBridgeAsync_ConcurrentCallsShareOneFailedImportThenRecover()
    {
        var gate = new TaskCompletionSource();
        var runtime = new FakeRuntime { ImportGate = gate };
        runtime.ImportFailures.Enqueue(new JSException("import failed"));
        await using var interop = new DeviceTimeFormatterInterop(runtime);

        var first = interop.EnsureBridgeAsync();
        var second = interop.EnsureBridgeAsync();
        runtime.ImportedPaths.Should().ContainSingle();

        gate.SetResult();

        await FluentActions.Awaiting(() => Task.WhenAll(first, second)).Should().ThrowAsync<JSException>();
        runtime.ImportedPaths.Should().ContainSingle();

        (await interop.EnsureBridgeAsync()).Should().BeTrue();
        runtime.ImportedPaths.Should().HaveCount(2);
        runtime.Module.Identifiers.Should().ContainSingle();
    }

    [Test]
    public async Task DisposeAsync_WhileTheImportIsPendingDoesNotThrowWhenItFails()
    {
        var gate = new TaskCompletionSource();
        var runtime = new FakeRuntime { ImportGate = gate };
        runtime.ImportFailures.Enqueue(new JSException("import failed"));
        var interop = new DeviceTimeFormatterInterop(runtime);

        var format = interop.FormatAsync(
            [new DeviceTimeFormatRequest(Guid.NewGuid(), 1, Detail: false)],
            new MessageTimePreferences());
        var dispose = interop.DisposeAsync().AsTask();

        gate.SetResult();

        await FluentActions.Awaiting(() => format).Should().ThrowAsync<JSException>();
        await FluentActions.Awaiting(() => dispose).Should().NotThrowAsync();
        runtime.Module.DisposeCount.Should().Be(0);
    }

    [Test]
    public async Task DisposeAsync_WhileTheImportIsPendingDoesNotThrowWhenItIsCanceled()
    {
        var gate = new TaskCompletionSource();
        var runtime = new FakeRuntime { ImportGate = gate };
        runtime.ImportFailures.Enqueue(new TaskCanceledException("import canceled"));
        var interop = new DeviceTimeFormatterInterop(runtime);

        var format = interop.FormatAsync(
            [new DeviceTimeFormatRequest(Guid.NewGuid(), 1, Detail: false)],
            new MessageTimePreferences());
        var dispose = interop.DisposeAsync().AsTask();

        gate.SetResult();

        await FluentActions.Awaiting(() => format).Should().ThrowAsync<TaskCanceledException>();
        await FluentActions.Awaiting(() => dispose).Should().NotThrowAsync();
        runtime.Module.DisposeCount.Should().Be(0);
    }

    [Test]
    public async Task DisposedFormatter_DoesNotTouchJsAgain()
    {
        var runtime = new FakeRuntime();
        var interop = new DeviceTimeFormatterInterop(runtime);

        (await interop.EnsureBridgeAsync()).Should().BeTrue();
        await interop.DisposeAsync();
        await interop.DisposeAsync();

        (await interop.EnsureBridgeAsync()).Should().BeFalse();
        (await interop.FormatAsync(
            [new DeviceTimeFormatRequest(Guid.NewGuid(), 1, Detail: false)],
            new MessageTimePreferences())).Should().BeNull();

        runtime.Module.DisposeCount.Should().Be(1);
        runtime.Module.Identifiers.Should().ContainSingle();
    }

    [Test]
    public async Task FormatAsync_DisposedAfterTheInitialCheckDoesNotImport()
    {
        var runtime = new FakeRuntime();
        var interop = new DeviceTimeFormatterInterop(runtime);
        var items = new DisposeOnCountList(
            interop,
            [new DeviceTimeFormatRequest(Guid.NewGuid(), 1, Detail: false)]);

        var result = await interop.FormatAsync(items, new MessageTimePreferences());

        result.Should().BeNull();
        runtime.ImportedPaths.Should().BeEmpty();
    }

    private sealed class DisposeOnCountList(
        DeviceTimeFormatterInterop interop,
        IReadOnlyList<DeviceTimeFormatRequest> items) : IReadOnlyList<DeviceTimeFormatRequest>
    {
        private bool _disposed;

        public int Count
        {
            get
            {
                if (!_disposed)
                {
                    _disposed = true;
                    interop.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                return items.Count;
            }
        }

        public DeviceTimeFormatRequest this[int index] => items[index];

        public IEnumerator<DeviceTimeFormatRequest> GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FakeRuntime : IJSRuntime
    {
        internal FakeModule Module { get; } = new();

        internal List<string?> ImportedPaths { get; } = [];

        internal Queue<Exception> ImportFailures { get; } = new();

        internal TaskCompletionSource? ImportGate { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            identifier.Should().Be("import");
            ImportedPaths.Add(args is { Length: > 0 } ? args[0] as string : null);
            return new ValueTask<TValue>(ResolveAsync<TValue>());
        }

        private async Task<TValue> ResolveAsync<TValue>()
        {
            if (ImportGate is { } gate)
                await gate.Task;
            if (ImportFailures.Count > 0)
                throw ImportFailures.Dequeue();
            return (TValue)(object)Module;
        }
    }

    private sealed class FakeModule : IJSObjectReference
    {
        internal List<string> Identifiers { get; } = [];

        internal List<object?[]> Arguments { get; } = [];

        internal DeviceTimeFormatResult? Result { get; } = new(
            "UTC",
            "Coordinated Universal Time",
            []);

        internal int DisposeCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Identifiers.Add(identifier);
            Arguments.Add(args ?? []);
            if (identifier == "installTimeBridge")
                return default;
            return new ValueTask<TValue>(Task.FromResult((TValue)(object)Result!));
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
