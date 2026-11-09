using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Browser;

internal sealed class FakeKeyboardJSRuntime : IJSRuntime
{
    internal FakeKeyboardModule Module { get; } = new();

    internal List<string?> ImportedPaths { get; } = [];

    internal bool ModuleUnavailable { get; init; }

    internal bool DisconnectedOnImport { get; init; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (identifier != "import")
            return default;

        ImportedPaths.Add(args is { Length: > 0 } ? args[0] as string : null);
        if (DisconnectedOnImport)
            throw new JSDisconnectedException("The circuit is gone.");

        if (ModuleUnavailable)
            return new ValueTask<TValue>(Task.FromResult(default(TValue)!));

        return new ValueTask<TValue>(Task.FromResult((TValue)(object)Module));
    }
}

internal sealed class FakeKeyboardModule : IJSObjectReference
{
    private int _nextAttachment = 1;

    internal List<string> Identifiers { get; } = [];

    internal List<object?[]> Arguments { get; } = [];

    internal HashSet<string> DisconnectedOn { get; } = [];

    internal int DisposeCount { get; private set; }

    internal bool DisconnectedOnDispose { get; set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        Identifiers.Add(identifier);
        Arguments.Add(args ?? []);

        if (DisconnectedOn.Contains(identifier))
            throw new JSDisconnectedException("The circuit is gone.");

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
        if (DisconnectedOnDispose)
            throw new JSDisconnectedException("The circuit is gone.");

        return default;
    }
}
