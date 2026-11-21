using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MqttProbe.UI.Tests.Components.Browser;

public sealed record KeyboardInteropSubject(
    string Name,
    string ModuleUrl,
    Func<IJSRuntime, IAsyncDisposable> Create,
    Func<IAsyncDisposable, ElementReference, Task> AttachAsync,
    Func<IAsyncDisposable, Task> DetachAsync)
{
    public override string ToString() => Name;
}

internal static class KeyboardInteropSubjects
{
    private static readonly KeyboardInteropSubject _jsonTree = new(
        "json-tree",
        "./_content/MqttProbe.UI/js/json-tree-keyboard.js",
        runtime => new JsonTreeKeyboardInterop(runtime),
        (interop, root) => ((JsonTreeKeyboardInterop)interop).AttachAsync(root),
        interop => ((JsonTreeKeyboardInterop)interop).DetachAsync());

    private static readonly KeyboardInteropSubject _messageList = new(
        "message-list",
        "./_content/MqttProbe.UI/js/message-list-keyboard.js",
        runtime => new MessageListKeyboardInterop(runtime),
        (interop, list) => ((MessageListKeyboardInterop)interop).AttachAsync(list),
        interop => ((MessageListKeyboardInterop)interop).DetachAsync());

    internal static IEnumerable<KeyboardInteropSubject> All()
    {
        yield return _jsonTree;
        yield return _messageList;
    }
}
