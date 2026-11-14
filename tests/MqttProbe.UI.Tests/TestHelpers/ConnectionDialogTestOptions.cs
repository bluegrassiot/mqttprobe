using MudBlazor;

namespace MqttProbe.UI.Tests.TestHelpers;

internal static class ConnectionDialogTestOptions
{
    internal static DialogOptions Production => new()
    {
        CloseOnEscapeKey = false,
        CloseButton = false,
        NoHeader = true,
        MaxWidth = MaxWidth.Small,
        FullWidth = true
    };
}
