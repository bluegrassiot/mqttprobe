using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Components.Browser;

internal sealed class MessageListKeyboardInterop(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MqttProbe.UI/js/message-list-keyboard.js";

    private readonly KeyboardInteropLifecycle _lifecycle = new(js, ModulePath);

    internal Task AttachAsync(ElementReference list) => _lifecycle.AttachAsync(list);

    internal Task DetachAsync() => _lifecycle.DetachAsync();

    internal async Task RevealAsync(ElementReference list, int index)
    {
        if (index < 0)
            return;

        await _lifecycle.InvokeVoidAsync("reveal", list, index);
    }

    public ValueTask DisposeAsync() => _lifecycle.DisposeAsync();
}
