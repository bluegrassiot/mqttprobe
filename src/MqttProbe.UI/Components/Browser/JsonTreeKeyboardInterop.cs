using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Browser;

internal sealed class JsonTreeKeyboardInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/json-tree-keyboard.js";

    private readonly KeyboardInteropLifecycle _lifecycle = new(js, ModulePath);

    internal Task AttachAsync(ElementReference root) => _lifecycle.AttachAsync(root);

    internal Task DetachAsync() => _lifecycle.DetachAsync();

    public ValueTask DisposeAsync() => _lifecycle.DisposeAsync();
}
