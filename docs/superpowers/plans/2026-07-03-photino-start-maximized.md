# Photino Start Maximized Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Desktop Photino window open maximized on launch. Keep 1280×800 as the restored (un-maximized) size. No fullscreen or chromeless.

**Architecture:** `src/MqttProbe.Desktop/Program.cs` builds a `PhotinoBlazorApp` and configures the main window via a fluent chain on `app.MainWindow`. Photino exposes `SetMaximized(bool)` which calls `ShowWindow(SW_MAXIMIZE)` on Windows and sets `_NET_WM_STATE_MAXIMIZED` on Linux. It does not alter the logical width/height — restoring the window returns to 1280×800.

```
src/MqttProbe.Desktop/
└── Program.cs                    ← add .SetMaximized(true)
```

---

## Task 1 — Add `.SetMaximized(true)` to the window-setup chain

**Why:** Users must manually maximize on every launch. `SetMaximized(true)` opens the window maximized while preserving 1280×800 as the restored geometry.

### Steps

- [ ] **1.1** In `src/MqttProbe.Desktop/Program.cs`, insert `.SetMaximized(true)` between `.SetHeight(800)` and `.SetIconFile(...)` on lines 91–97.

  **Before:**

  ```csharp
  app.MainWindow
      .SetTitle("")
      .SetWidth(1280)
      .SetHeight(800)
      .SetIconFile(Path.Combine(AppContext.BaseDirectory, "Assets", iconFile))
      .RegisterWindowCreatedHandler((_, _) =>
          WindowsTitleBar.ApplyBrandTint(app.MainWindow.WindowHandle));
  ```

  **After:**

  ```csharp
  app.MainWindow
      .SetTitle("")
      .SetWidth(1280)
      .SetHeight(800)
      .SetMaximized(true)
      .SetIconFile(Path.Combine(AppContext.BaseDirectory, "Assets", iconFile))
      .RegisterWindowCreatedHandler((_, _) =>
          WindowsTitleBar.ApplyBrandTint(app.MainWindow.WindowHandle));
  ```

- [ ] **1.2** Build to confirm no errors:

  ```powershell
  dotnet build src/MqttProbe.Desktop/MqttProbe.Desktop.csproj --warnaserror
  ```

  **Expected result:** Build succeeds with 0 warnings, 0 errors.

- [ ] **1.3** Run the full test suite to confirm no regressions:

  ```powershell
  dotnet test tests/MqttProbe.Tests
  ```

  **Expected result:** All tests pass. (No Desktop-window integration tests exist, so no regressions possible from this change.)

- [ ] **1.4** Commit:

  ```
  git add src/MqttProbe.Desktop/Program.cs
  git commit -m "desktop: open window maximized on launch"
  ```

---

## Summary of all files changed

| File | Change type | Task |
|------|-------------|------|
| `src/MqttProbe.Desktop/Program.cs` | Edit — insert `.SetMaximized(true)` after `.SetHeight(800)` | 1 |
