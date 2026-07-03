# Photino Desktop Start Maximized

## Context

The Desktop host opens at a fixed 1280×800 window. Users on typical monitors must manually maximize on every launch. The native window should open maximized so the app fills the screen immediately, while preserving 1280×800 as the restored (un-maximized) size for users who later un-maximize.

## Goals

- Desktop window opens maximized on launch using Photino's `SetMaximized(true)`.
- Existing `SetWidth(1280)` / `SetHeight(800)` remain as the restored-window geometry.
- Behavior is scoped to the Desktop host only; Web and MAUI hosts are unchanged.

## Non-goals

- No fullscreen mode (OS chrome/taskbar remains visible).
- No chromeless or borderless window treatment.
- No persistent window-state storage (remembering maximized vs. restored across sessions).
- No changes to `SetIconFile`, `SetTitle`, `RegisterWindowCreatedHandler`, or any service registrations.

## Design

In `src/MqttProbe.Desktop/Program.cs`, insert `.SetMaximized(true)` into the existing fluent window-setup chain between `.SetHeight(800)` and `.SetIconFile(...)`:

```csharp
app.MainWindow
    .SetTitle("")
    .SetWidth(1280)
    .SetHeight(800)
    .SetMaximized(true)                // ← new
    .SetIconFile(Path.Combine(AppContext.BaseDirectory, "Assets", iconFile))
    .RegisterWindowCreatedHandler((_, _) =>
        WindowsTitleBar.ApplyBrandTint(app.MainWindow.WindowHandle));
```

`SetMaximized(true)` is a Photino native API call (P/Invoke to `ShowWindow(SW_MAXIMIZE)` on Windows, X11/Wayland `_NET_WM_STATE_MAXIMIZED` on Linux). It does not affect the logical width/height — when the user restores the window, it returns to 1280×800.

## Files affected

| File | Change |
|------|--------|
| `src/MqttProbe.Desktop/Program.cs` | Add `.SetMaximized(true)` to window-setup chain (1 line) |

No other files require modification.

## Verification

1. `dotnet build src/MqttProbe.Desktop/MqttProbe.Desktop.csproj --warnaserror` — succeeds with 0 warnings.
2. `dotnet test tests/MqttProbe.Tests` — all existing tests pass (no Desktop-window integration tests exist, so no regressions possible).
3. Manual: launch the Desktop app; window should open maximized. Click restore/title-bar un-maximize button; window should return to 1280×800.
